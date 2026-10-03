using Mutagen.Bethesda.Plugins.Cache;

namespace Antigen.SDK.Caches;

/// <summary>
/// A cache made in parts that can be made at once on several threads, so a host can spread a large cache over its
/// workers rather than leave one making it. <see cref="ICacheConstructor.Construct"/> still makes it whole.
/// </summary>
public interface IPartitionedCacheConstructor : ICacheConstructor
{
    /// <summary>Works out the parts: what's needed to know how many there are, and nothing more.</summary>
    ICachePlan Plan(ILinkCache linkCache, IProvideCaches provideCaches);
}

/// <summary>A cache being made in parts (<see cref="IPartitionedCacheConstructor"/>).</summary>
public interface ICachePlan
{
    /// <summary>How many parts there are.</summary>
    int Count { get; }

    /// <summary>Makes the parts from <paramref name="start"/> to before <paramref name="end"/>; called on several threads at once, for different parts.</summary>
    void Build(int start, int end);

    /// <summary>The cache, once every part is made.</summary>
    object Seal();
}

public static class CachePlanExtensions
{
    /// <summary>Makes a plan's parts in parallel, then the cache.</summary>
    public static object BuildAll(this ICachePlan plan)
    {
        Parallel.For(0, plan.Count, i => plan.Build(i, i + 1));
        return plan.Seal();
    }
}