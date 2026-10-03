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
/// A set of speakers: NPCs and talking activators, as bits by their place in the load order's, and any others by FormKey.
/// </summary>
public sealed class SpeakerSet
{
    private readonly ulong[] _bits;
    private readonly HashSet<FormKey>? _others;

    internal SpeakerSet(ulong[] bits, HashSet<FormKey>? others)
    {
        _bits = bits;
        _others = others;
        IsEmpty = others is null && Array.TrueForAll(bits, static w => w == 0);
    }

    public bool IsEmpty { get; }

    /// <summary>Whether any speaker is in both.</summary>
    public bool Intersects(SpeakerSet other)
    {
        var words = Math.Min(_bits.Length, other._bits.Length);
        for (var i = 0; i < words; i++)
        {
            if ((_bits[i] & other._bits[i]) != 0) return true;
        }
        return _others is not null && other._others is not null && _others.Overlaps(other._others);
    }
}

public class SpeakerCacheProvider : IPartitionedCacheConstructor, IUsesCaches
{
    public Type CacheType => typeof(ISpeakerCache);

    public IEnumerable<Type> Caches => [typeof(VoiceTypeAssetLookup)];

    public object Construct(ILinkCache linkCache, IProvideCaches provideCaches) => Plan(linkCache, provideCaches).BuildAll();

    /// <summary>In two stages: a part for each plugin, listing its speakers and responses; then one for each response.</summary>
    public ICachePlan Plan(ILinkCache linkCache, IProvideCaches provideCaches) =>
        new SpeakerCache.Listed(linkCache, provideCaches.Resolve<VoiceTypeAssetLookup>());
}

/// <summary>Made in a part for each dialog response (<see cref="Planned"/>), from its winning version.</summary>
public class SpeakerCache : ISpeakerCache
{
    private readonly VoiceTypeAssetLookup _lookup;
    private readonly IReadOnlyDictionary<FormKey, int> _speakerIndex;
    private readonly IReadOnlyDictionary<FormKey, (ModKey Plugin, SpeakerSet Speakers)> _winners;

    private SpeakerCache(VoiceTypeAssetLookup lookup, IReadOnlyDictionary<FormKey, int> speakerIndex,
        IReadOnlyDictionary<FormKey, (ModKey, SpeakerSet)> winners)
    {
        _lookup = lookup;
        _speakerIndex = speakerIndex;
        _winners = winners;
    }

    public SpeakerSet? Get(FormKey responses) => _winners.TryGetValue(responses, out var winner) ? winner.Speakers : null;

    public SpeakerSet For(IDialogResponsesGetter responses, ModKey plugin) =>
        _winners.TryGetValue(responses.FormKey, out var winner) && winner.Plugin == plugin ? winner.Speakers : Of(responses);

    public SpeakerSet Of(IDialogResponsesGetter responses) => Of(_lookup, _speakerIndex, responses);

    private static SpeakerSet Of(VoiceTypeAssetLookup lookup, IReadOnlyDictionary<FormKey, int> index, IDialogResponsesGetter responses)
    {
        var bits = new ulong[(index.Count + 63) / 64];
        HashSet<FormKey>? others = null;
        foreach (var speaker in lookup.GetSpeakers(responses))
        {
            if (index.TryGetValue(speaker.FormKey, out var at)) bits[at >> 6] |= 1UL << (at & 63);
            else (others ??= []).Add(speaker.FormKey);
        }
        return new SpeakerSet(bits, others);
    }

    /// <summary>
    /// The cache's first stage: a part for each plugin, listing its NPCs and talking activators (who can speak) and its
    /// dialog responses. Sealed, the lists are put together in the load order's order, and the next stage planned.
    /// </summary>
    public sealed class Listed : ICachePlan
    {
        private readonly ILinkCache _linkCache;
        private readonly VoiceTypeAssetLookup _lookup;
        private readonly IModGetter[] _mods;
        private readonly (FormKey[] Speakers, FormKey[] Responses)[] _lists;

        public Listed(ILinkCache linkCache, VoiceTypeAssetLookup lookup)
        {
            _linkCache = linkCache;
            _lookup = lookup;
            _mods = [.. linkCache.PriorityOrder];
            _lists = new (FormKey[], FormKey[])[_mods.Length];
        }

        public int Count => _mods.Length;

        public void Build(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                var mod = _mods[i];
                _lists[i] = (
                    [.. mod.EnumerateMajorRecords<INpcGetter>().Select(n => n.FormKey)
                        .Concat(mod.EnumerateMajorRecords<ITalkingActivatorGetter>().Select(t => t.FormKey))],
                    [.. mod.EnumerateMajorRecords<IDialogResponsesGetter>().Select(r => r.FormKey)]);
            }
        }

        public object Seal()
        {
            // Each speaker a bit of a set, and each response once.
            var speakerIndex = new Dictionary<FormKey, int>();
            foreach (var (speakers, _) in _lists)
            {
                foreach (var speaker in speakers) speakerIndex.TryAdd(speaker, speakerIndex.Count);
            }
            var responses = new HashSet<FormKey>();
            foreach (var (_, ofMod) in _lists) responses.UnionWith(ofMod);
            return new Planned(_linkCache, _lookup, speakerIndex, [.. responses]);
        }
    }

    /// <summary>The cache's second stage: its parts are the load order's dialog responses.</summary>
    public sealed class Planned : ICachePlan
    {
        private readonly ILinkCache _linkCache;
        private readonly VoiceTypeAssetLookup _lookup;
        private readonly Dictionary<FormKey, int> _speakerIndex;
        private readonly FormKey[] _responses;
        private readonly (ModKey Plugin, SpeakerSet Speakers)?[] _found;

        public Planned(ILinkCache linkCache, VoiceTypeAssetLookup lookup, Dictionary<FormKey, int> speakerIndex, FormKey[] responses)
        {
            _linkCache = linkCache;
            _lookup = lookup;
            _speakerIndex = speakerIndex;
            _responses = responses;
            _found = new (ModKey, SpeakerSet)?[_responses.Length];
        }

        public int Count => _responses.Length;

        public void Build(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (!_linkCache.TryResolveSimpleContext<IDialogResponsesGetter>(_responses[i], out var context)) continue;
                _found[i] = (context.ModKey, Of(_lookup, _speakerIndex, context.Record));
            }
        }

        public object Seal()
        {
            var winners = new Dictionary<FormKey, (ModKey, SpeakerSet)>(_responses.Length);
            for (var i = 0; i < _responses.Length; i++)
            {
                if (_found[i] is { } found) winners[_responses[i]] = found;
            }
            return new SpeakerCache(_lookup, _speakerIndex, winners);
        }
    }
}