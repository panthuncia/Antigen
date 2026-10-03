using Antigen.SDK.Caches;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Caches;

using WorldspaceLookup = IReadOnlyDictionary<P2Int, IFormLinkGetter<ICellGetter>>;

public interface IExteriorCellCache
{
    public IFormLinkGetter<ICellGetter> GetExterior(IFormLinkGetter<IWorldspaceGetter> worldspace, P2Int grid);
    public IFormLinkGetter<ICellGetter> GetExterior(IWorldspaceGetter worldspace, P2Int grid);
};

public class ExteriorCellCacheProvider : IPartitionedCacheConstructor
{
    public Type CacheType => typeof(IExteriorCellCache);

    public object Construct(ILinkCache linkCache, IProvideCaches provideCaches) => Plan(linkCache, provideCaches).BuildAll();

    /// <summary>A part for each version of each worldspace.</summary>
    public ICachePlan Plan(ILinkCache linkCache, IProvideCaches provideCaches) => new ImmutableExteriorCellCache.Planned(linkCache);
}

/// <summary>
/// Each worldspace's exterior cells by their place on its grid, made for every worldspace at once, a version of a
/// worldspace a part (<see cref="Planned"/>), rather than for each as it's first asked about while others asking wait.
/// </summary>
public class ImmutableExteriorCellCache : IExteriorCellCache
{
    private readonly IReadOnlyDictionary<FormKey, WorldspaceLookup> _worlds;

    public ImmutableExteriorCellCache(ILinkCache linkCache)
        : this(((ImmutableExteriorCellCache)new Planned(linkCache).BuildAll())._worlds)
    {
    }

    private ImmutableExteriorCellCache(IReadOnlyDictionary<FormKey, WorldspaceLookup> worlds)
    {
        _worlds = worlds;
    }

    /// <summary>Every worldspace's exterior cells, each with its worldspace and place.</summary>
    internal IEnumerable<(FormKey World, P2Int Point, IFormLinkGetter<ICellGetter> Cell)> Exteriors =>
        _worlds.SelectMany(w => w.Value.Select(e => (w.Key, e.Key, e.Value)));

    internal static WorldspaceLookup CreateLookupForWorld(ILinkCache linkCache, FormKey worldspace)
    {
        var lookup = new Dictionary<P2Int, IFormLinkGetter<ICellGetter>>();

        foreach (var worldspaceOverride in linkCache.ResolveAll<IWorldspaceGetter>(worldspace))
        {
            foreach (var (point, cell) in ExteriorsOf(worldspaceOverride))
            {
                lookup[point] = cell;
            }
        }
        return lookup;
    }

    /// <summary>A version of a worldspace's exterior cells, as it lists them.</summary>
    private static IEnumerable<(P2Int Point, IFormLinkGetter<ICellGetter> Cell)> ExteriorsOf(IWorldspaceGetter worldspace)
    {
        foreach (var block in worldspace.SubCells)
        {
            foreach (var subBlock in block.Items)
            {
                foreach (var cell in subBlock.Items)
                {
                    if (cell.Grid != null)
                    {
                        yield return (cell.Grid.Point, cell.ToLink());
                    }
                }
            }
        }
    }

    private IFormLinkGetter<ICellGetter> GetExterior(FormKey worldspace, P2Int grid) =>
        _worlds.TryGetValue(worldspace, out var lookup) && lookup.TryGetValue(grid, out var cell) ? cell : FormLink<ICellGetter>.Null;

    public IFormLinkGetter<ICellGetter> GetExterior(IFormLinkGetter<IWorldspaceGetter> worldspace, P2Int grid) =>
        GetExterior(worldspace.FormKey, grid);

    public IFormLinkGetter<ICellGetter> GetExterior(IWorldspaceGetter worldspace, P2Int grid) =>
        GetExterior(worldspace.FormKey, grid);

    /// <summary>
    /// The cache in parts: each version of each worldspace of the load order, its cells listed; sealed, each
    /// worldspace's are put together as <see cref="CreateLookupForWorld"/> does, in the order the versions resolve.
    /// </summary>
    public sealed class Planned : ICachePlan
    {
        private readonly (FormKey World, IWorldspaceGetter Version)[] _versions;
        private readonly (P2Int Point, IFormLinkGetter<ICellGetter> Cell)[][] _found;

        public Planned(ILinkCache linkCache)
        {
            _versions =
            [
                .. linkCache.AllIdentifiers<IWorldspaceGetter>().Select(w => w.FormKey).Distinct()
                    .SelectMany(w => linkCache.ResolveAll<IWorldspaceGetter>(w).Select(version => (w, version))),
            ];
            _found = new (P2Int, IFormLinkGetter<ICellGetter>)[_versions.Length][];
        }

        public int Count => _versions.Length;

        public void Build(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                _found[i] = [.. ExteriorsOf(_versions[i].Version)];
            }
        }

        public object Seal()
        {
            var worlds = new Dictionary<FormKey, Dictionary<P2Int, IFormLinkGetter<ICellGetter>>>();
            for (var i = 0; i < _versions.Length; i++)
            {
                if (!worlds.TryGetValue(_versions[i].World, out var lookup)) worlds[_versions[i].World] = lookup = [];
                foreach (var (point, cell) in _found[i])
                {
                    lookup[point] = cell;
                }
            }
            return new ImmutableExteriorCellCache(worlds.ToDictionary(w => w.Key, WorldspaceLookup (w) => w.Value));
        }
    }
}
