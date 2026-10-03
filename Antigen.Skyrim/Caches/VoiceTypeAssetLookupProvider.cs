using Antigen.SDK.Caches;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim.Records.Assets.VoiceType;

namespace Antigen.Skyrim.Caches;

public class VoiceTypeAssetLookupProvider : IPartitionedCacheConstructor
{
    public Type CacheType => typeof(VoiceTypeAssetLookup);

    public object Construct(
        ILinkCache linkCache,
        IProvideCaches provideCaches) => Plan(linkCache, provideCaches).BuildAll();

    /// <summary>A part for each plugin, reading its records (<see cref="VoiceTypeAssetLookup.Prepare"/>); sealed, they're put together.</summary>
    public ICachePlan Plan(ILinkCache linkCache, IProvideCaches provideCaches) => new Prepared(linkCache.CreateImmutableAssetLinkCache());

    private sealed class Prepared : ICachePlan
    {
        private readonly VoiceTypeAssetLookup _lookup = new();

        public Prepared(IAssetLinkCache linkCache)
        {
            Preparation = _lookup.Prepare(linkCache);
        }

        private VoiceTypeAssetLookup.Preparation Preparation { get; }

        public int Count => Preparation.Count;

        public void Build(int start, int end) => Preparation.Read(start, end);

        public object Seal()
        {
            Preparation.Finish();
            return _lookup;
        }
    }
}
