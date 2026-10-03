namespace Antigen.SDK.Caches;

/// <summary>
/// Says which caches an analyzer, or a cache's constructor, resolves (<see cref="IProvideCaches.Resolve{TAnalyzerCache}"/>),
/// so a host can make them before it runs the analyzer, rather than have every thread needing one wait while the first
/// makes it. An analyzer that doesn't implement this is taken to resolve none.
/// </summary>
public interface IUsesCaches
{
    /// <summary>The types of the caches resolved: each a <see cref="ICacheConstructor.CacheType"/>.</summary>
    IEnumerable<Type> Caches { get; }
}