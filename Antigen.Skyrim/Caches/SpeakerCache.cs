using System.Runtime.CompilerServices;
using Antigen.SDK.Caches;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Records.Assets.VoiceType;

namespace Antigen.Skyrim.Caches;

/// <summary>
/// Who can speak each dialog response (<see cref="VoiceTypeAssetLookup.GetSpeakers"/>), worked out once for each winning
/// response rather than again by every check and every topic linking to it, and held as sets cheap to compare.
/// </summary>
public interface ISpeakerCache
{
    /// <summary>The speakers of the winning version of a response; null when it isn't in the load order.</summary>
    SpeakerSet? Get(FormKey responses);

    /// <summary>
    /// The speakers of a version of a response: the cache's when it's the winning version (that of
    /// <paramref name="plugin"/>), worked out from it otherwise.
    /// </summary>
    SpeakerSet For(IDialogResponsesGetter responses, ModKey plugin);

    /// <summary>The speakers of a response as given, worked out now.</summary>
    SpeakerSet Of(IDialogResponsesGetter responses);
}

/// <summary>
/// A set of speakers (NPCs and talking activators) as <see cref="VoiceTypeAssetLookup.GetSpeakerVoices"/> gives them:
/// voice types whole, as bits, and speakers named one by one. Most responses are whole voice types (one without
/// conditions is every speaker), so a set is a few words rather than a bit for each speaker in the load order.
/// </summary>
public sealed class SpeakerSet
{
    private readonly ulong[] _whole;
    private readonly ulong[] _reach;
    private readonly ulong[] _namedVoices;
    private readonly HashSet<FormKey>? _named;

    /// <param name="whole">The voice types all of whose speakers are in the set.</param>
    /// <param name="reach">The voice types sharing a speaker with one of those (each of them among them).</param>
    /// <param name="namedVoices">The voice types of the speakers named.</param>
    /// <param name="named">The speakers named, or null for none.</param>
    internal SpeakerSet(ulong[] whole, ulong[] reach, ulong[] namedVoices, HashSet<FormKey>? named)
    {
        _whole = whole;
        _reach = reach;
        _namedVoices = namedVoices;
        _named = named;
        IsEmpty = named is null && Array.TrueForAll(whole, static w => w == 0);
    }

    public bool IsEmpty { get; }

    /// <summary>Whether any speaker is in both.</summary>
    public bool Intersects(SpeakerSet other) =>
        Overlap(_reach, other._whole)
        || Overlap(_namedVoices, other._whole)
        || Overlap(other._namedVoices, _whole)
        || (_named is not null && other._named is not null && _named.Overlaps(other._named));

    private static bool Overlap(ulong[] a, ulong[] b)
    {
        var words = Math.Min(a.Length, b.Length);
        for (var i = 0; i < words; i++)
        {
            if ((a[i] & b[i]) != 0) return true;
        }
        return false;
    }
}

/// <summary>
/// The load order's voice types as bits, and which share a speaker: what <see cref="SpeakerSet"/>s are made with.
/// </summary>
public sealed class SpeakerVoices
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly ulong[][] _overlaps;
    private readonly Func<FormKey, IReadOnlyCollection<string>> _voiceTypesOf;
    private readonly int _words;

    /// <param name="voiceTypes">Every voice type a speaker has.</param>
    /// <param name="speakersOf">The speakers of a voice type.</param>
    /// <param name="voiceTypesOf">A speaker's voice types: none for a FormKey that isn't a speaker.</param>
    public SpeakerVoices(IEnumerable<string> voiceTypes, Func<string, IReadOnlyCollection<FormKey>> speakersOf,
        Func<FormKey, IReadOnlyCollection<string>> voiceTypesOf)
    {
        _voiceTypesOf = voiceTypesOf;
        foreach (var voiceType in voiceTypes)
        {
            if (speakersOf(voiceType).Count > 0) _index.TryAdd(voiceType, _index.Count);
        }
        _words = (_index.Count + 63) / 64;
        _overlaps = new ulong[_index.Count][];
        foreach (var (voiceType, at) in _index)
        {
            var overlaps = _overlaps[at] = new ulong[_words];
            foreach (var speaker in speakersOf(voiceType))
            {
                Set(overlaps, voiceTypesOf(speaker));
            }
        }
    }

    private static readonly SpeakerSet None = new([], [], [], null);

    // The sets of frozen containers, which the lookup shares among many responses (all the default voices, for one
    // without conditions): each made once.
    private readonly ConditionalWeakTable<VoiceContainer, SpeakerSet> _shared = new();

    /// <summary>The speakers <paramref name="voices"/> holds: none for null.</summary>
    public SpeakerSet Of(VoiceContainer? voices)
    {
        if (voices is null || voices.Voices.Count == 0) return None;
        if (!voices.IsFrozen) return Make(voices);
        if (_shared.TryGetValue(voices, out var made)) return made;
        return _shared.GetValue(voices, Make);
    }

    private SpeakerSet Make(VoiceContainer voices)
    {
        ulong[]? whole = null;
        ulong[]? reach = null;
        ulong[]? namedVoices = null;
        HashSet<FormKey>? named = null;
        foreach (var (voiceType, speakers) in voices.Voices)
        {
            if (speakers.Count > 0)
            {
                foreach (var speaker in speakers)
                {
                    if ((named ??= []).Add(speaker)) Set(namedVoices ??= new ulong[_words], _voiceTypesOf(speaker));
                }
            }
            // A voice type without speakers adds none.
            else if (_index.TryGetValue(voiceType, out var at))
            {
                (whole ??= new ulong[_words])[at >> 6] |= 1UL << (at & 63);
                reach ??= new ulong[_words];
                var overlaps = _overlaps[at];
                for (var i = 0; i < _words; i++) reach[i] |= overlaps[i];
            }
        }
        // Words left out are none (SpeakerSet compares as many words as both have).
        return new SpeakerSet(whole ?? [], reach ?? [], namedVoices ?? [], named);
    }

    private void Set(ulong[] bits, IReadOnlyCollection<string> voiceTypes)
    {
        // The lookup's are sets: enumerated as such, without boxing an enumerator for each speaker.
        if (voiceTypes is HashSet<string> set)
        {
            foreach (var voiceType in set) Set(bits, voiceType);
        }
        else
        {
            foreach (var voiceType in voiceTypes) Set(bits, voiceType);
        }
    }

    private void Set(ulong[] bits, string voiceType)
    {
        if (_index.TryGetValue(voiceType, out var at)) bits[at >> 6] |= 1UL << (at & 63);
    }
}

public class SpeakerCacheProvider : IPartitionedCacheConstructor, IUsesCaches
{
    public Type CacheType => typeof(ISpeakerCache);

    public IEnumerable<Type> Caches => [typeof(VoiceTypeAssetLookup)];

    public object Construct(ILinkCache linkCache, IProvideCaches provideCaches) => Plan(linkCache, provideCaches).BuildAll();

    /// <summary>In two stages: a part for each plugin, listing its responses; then one for each response.</summary>
    public ICachePlan Plan(ILinkCache linkCache, IProvideCaches provideCaches) =>
        new SpeakerCache.Listed(linkCache, provideCaches.Resolve<VoiceTypeAssetLookup>());
}

/// <summary>Made in a part for each dialog response (<see cref="Planned"/>), from its winning version.</summary>
public class SpeakerCache : ISpeakerCache
{
    private readonly VoiceTypeAssetLookup _lookup;
    private readonly SpeakerVoices _voices;
    private readonly IReadOnlyDictionary<FormKey, (ModKey Plugin, SpeakerSet Speakers)> _winners;

    private SpeakerCache(VoiceTypeAssetLookup lookup, SpeakerVoices voices, IReadOnlyDictionary<FormKey, (ModKey, SpeakerSet)> winners)
    {
        _lookup = lookup;
        _voices = voices;
        _winners = winners;
    }

    public SpeakerSet? Get(FormKey responses) => _winners.TryGetValue(responses, out var winner) ? winner.Speakers : null;

    public SpeakerSet For(IDialogResponsesGetter responses, ModKey plugin) =>
        _winners.TryGetValue(responses.FormKey, out var winner) && winner.Plugin == plugin ? winner.Speakers : Of(responses);

    public SpeakerSet Of(IDialogResponsesGetter responses) => _voices.Of(_lookup.GetSpeakerVoices(responses));

    /// <summary>
    /// The cache's first stage: a part for each plugin, listing its dialog responses. Sealed, the load order's voice
    /// types are numbered, and the next stage planned.
    /// </summary>
    public sealed class Listed : ICachePlan
    {
        private readonly ILinkCache _linkCache;
        private readonly VoiceTypeAssetLookup _lookup;
        private readonly IModGetter[] _mods;
        private readonly FormKey[][] _responses;

        public Listed(ILinkCache linkCache, VoiceTypeAssetLookup lookup)
        {
            _linkCache = linkCache;
            _lookup = lookup;
            _mods = [.. linkCache.PriorityOrder];
            _responses = new FormKey[_mods.Length][];
        }

        public int Count => _mods.Length;

        public void Build(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                _responses[i] = [.. _mods[i].EnumerateMajorRecords<IDialogResponsesGetter>().Select(r => r.FormKey)];
            }
        }

        public object Seal()
        {
            // Each response once.
            var responses = new HashSet<FormKey>();
            foreach (var ofMod in _responses) responses.UnionWith(ofMod);
            var voices = new SpeakerVoices(_lookup.VoiceTypes, _lookup.GetSpeakersOfVoiceType, _lookup.GetVoiceTypesOfSpeaker);
            return new Planned(_linkCache, _lookup, voices, [.. responses]);
        }
    }

    /// <summary>The cache's second stage: its parts are the load order's dialog responses.</summary>
    public sealed class Planned : ICachePlan
    {
        private readonly ILinkCache _linkCache;
        private readonly VoiceTypeAssetLookup _lookup;
        private readonly SpeakerVoices _voices;
        private readonly FormKey[] _responses;
        private readonly (ModKey Plugin, SpeakerSet Speakers)?[] _found;

        public Planned(ILinkCache linkCache, VoiceTypeAssetLookup lookup, SpeakerVoices voices, FormKey[] responses)
        {
            _linkCache = linkCache;
            _lookup = lookup;
            _voices = voices;
            _responses = responses;
            _found = new (ModKey, SpeakerSet)?[_responses.Length];
        }

        public int Count => _responses.Length;

        public void Build(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (!_linkCache.TryResolveSimpleContext<IDialogResponsesGetter>(_responses[i], out var context)) continue;
                _found[i] = (context.ModKey, _voices.Of(_lookup.GetSpeakerVoices(context.Record)));
            }
        }

        public object Seal()
        {
            var winners = new Dictionary<FormKey, (ModKey, SpeakerSet)>(_responses.Length);
            for (var i = 0; i < _responses.Length; i++)
            {
                if (_found[i] is { } found) winners[_responses[i]] = found;
            }
            return new SpeakerCache(_lookup, _voices, winners);
        }
    }
}
