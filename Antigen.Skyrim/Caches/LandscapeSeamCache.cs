using Antigen.SDK.Caches;
using Antigen.Skyrim.Extensions;
using Antigen.Skyrim.Record.Landscape;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Caches;

/// <summary>
/// What the landscape seam checks need of every exterior cell, worked out once for the whole load order rather than
/// again by each of a landscape's neighbours: which cells are near a border region, and the edges of each cell's winning
/// landscape.
/// </summary>
public interface ILandscapeSeamCache
{
    /// <summary>
    /// Whether the cell at a point of a worldspace is in or within two cells of a border region, as
    /// <see cref="CellExtensions.IsNearBorderRegion"/> finds.
    /// </summary>
    bool IsNearBorderRegion(FormKey worldspace, P2Int point);

    /// <summary>
    /// The edges of the winning landscape of the cell at a point: null when there's no cell or landscape there. A landscape
    /// whose data couldn't be decoded has <see cref="LandscapeEdges.Failed"/> set.
    /// </summary>
    LandscapeEdges? GetEdges(FormKey worldspace, P2Int point);
}

public class LandscapeSeamCacheProvider : ICacheConstructor
{
    public Type CacheType => typeof(ILandscapeSeamCache);

    public object Construct(ILinkCache linkCache, IProvideCaches provideCaches) =>
        new LandscapeSeamCache(linkCache, provideCaches.Resolve<ILinkUsageCache>());
}

/// <summary>
/// The edges of a landscape that its neighbours compare with theirs, copied out of its decoded data: heights, vertex
/// colours, and the opacity of each texture of each quadrant. An edge of all zeros is held as null.
/// </summary>
public sealed class LandscapeEdges
{
    /// <summary>The plugin of the landscape's version these are of.</summary>
    public required ModKey Plugin { get; init; }

    public required FormKey Landscape { get; init; }

    /// <summary>The height map's edge in each direction, or null without a height map.</summary>
    public float[]?[]? Heights { get; init; }

    /// <summary>The vertex colours' edge in each direction, the default colours' without any.</summary>
    public P3UInt8[]?[] Colors { get; init; } = [];

    /// <summary>Each quadrant's layers, by <see cref="Quadrant"/>.</summary>
    public QuadrantEdges[] Quadrants { get; init; } = [];

    /// <summary>What decoding the landscape threw, to throw again where it's used; null when it decoded.</summary>
    public Exception? Failed { get; init; }

    /// <summary>A landscape's edges, decoded from it. Throws as decoding it does.</summary>
    public static LandscapeEdges Of(ILandscapeGetter landscape, ModKey plugin)
    {
        var heights = landscape.VertexHeightMap?.Decode();
        var colors = landscape.VertexColors ?? LandscapeSeamAnalyzer.DefaultVertexColors;
        return new LandscapeEdges
        {
            Plugin = plugin,
            Landscape = landscape.FormKey,
            Heights = heights is null ? null : Edges(heights, static e => e),
            Colors = Edges(colors, static e => e),
            Quadrants = [.. Enum.GetValues<Quadrant>().Select(q => QuadrantEdges.Of(landscape.Layers.DecodeQuadrant(q)))],
        };
    }

    internal static T[]?[] Edges<T>(IReadOnlyArray2d<T> data, Func<T[], T[]?> keep) =>
        [.. LandscapeSeamAnalyzer.Directions.Select(d => keep(LandscapeSeamAnalyzer.GetEdge(data, d).ToArray()))];
}

/// <summary>A quadrant's layers as its edges: each texture's opacity along each side, in the order of its layers.</summary>
public sealed class QuadrantEdges
{
    public required Quadrant Quadrant { get; init; }

    public required IReadOnlyList<IFormLinkGetter<ILandscapeTextureGetter>> Textures { get; init; }

    /// <summary>For each layer, its opacity's edge in each direction; null for an edge of all zeros.</summary>
    public required float[]?[][] Opacity { get; init; }

    public static QuadrantEdges Of(LandscapeExtensions.QuadrantData quadrant) => new()
    {
        Quadrant = quadrant.Quadrant,
        Textures = [.. quadrant.Layers.Select(l => l.Texture)],
        Opacity = [.. quadrant.Layers.Select(l => LandscapeEdges.Edges(l.Opacity, static e => e.All(o => o == 0) ? null : e))],
    };

    /// <summary>
    /// A texture's opacity along a side: that of the first layer with the texture, as
    /// <see cref="LandscapeExtensions.QuadrantData.GetLayer"/> finds it, and zeros without one.
    /// </summary>
    public float[] Edge(IFormLinkGetter<ILandscapeTextureGetter> texture, LandscapeSeamAnalyzer.Direction direction)
    {
        for (var i = 0; i < Textures.Count; i++)
        {
            if (Textures[i].Equals(texture)) return Opacity[i][(int)direction] ?? Zeros;
        }
        return Zeros;
    }

    private static readonly float[] Zeros = new float[LandscapeExtensions.QuadSize];
}

/// <summary>
/// Made at once, a worldspace at a time and its cells in parallel, from the winning versions: each worldspace's exterior
/// cells, which of them are in a border region, and their landscapes' edges.
/// </summary>
public class LandscapeSeamCache : ILandscapeSeamCache
{
    private readonly Dictionary<FormKey, World> _worlds = [];

    private sealed record World(HashSet<P2Int> NearBorder, Dictionary<P2Int, LandscapeEdges> Edges);

    public LandscapeSeamCache(ILinkCache linkCache, ILinkUsageCache usageCache)
    {
        var worldspaces = linkCache.PriorityOrder.SelectMany(m => m.EnumerateMajorRecords<IWorldspaceGetter>())
            .Select(w => w.FormKey)
            .Distinct()
            .ToArray();
        // Whether a worldspace has any border region; asked of every cell in it, so found once for each.
        var hasBorder = new System.Collections.Concurrent.ConcurrentDictionary<FormKey, bool>();
        foreach (var worldspace in worldspaces)
        {
            var exteriors = ImmutableExteriorCellCache.CreateLookupForWorld(linkCache, worldspace).ToArray();
            var inBorder = new bool[exteriors.Length];
            var edges = new LandscapeEdges?[exteriors.Length];
            Parallel.For(0, exteriors.Length, i =>
            {
                if (!exteriors[i].Value.TryResolve(linkCache, out var cell)) return;
                inBorder[i] = IsInBorderRegion(cell, linkCache, usageCache, hasBorder);
                if (cell.GetLandscape(linkCache) is not { } landscape) return;
                var plugin = linkCache.TryResolveSimpleContext(landscape, out var context) ? context.ModKey : landscape.FormKey.ModKey;
                try
                {
                    edges[i] = LandscapeEdges.Of(landscape, plugin);
                }
                catch (Exception e)
                {
                    edges[i] = new LandscapeEdges { Plugin = plugin, Landscape = landscape.FormKey, Failed = e };
                }
            });

            var near = new HashSet<P2Int>();
            var world = new World(near, []);
            for (var i = 0; i < exteriors.Length; i++)
            {
                if (edges[i] is { } found) world.Edges[exteriors[i].Key] = found;
                if (!inBorder[i]) continue;
                var point = exteriors[i].Key;
                for (var x = -2; x <= 2; x++)
                {
                    for (var y = -2; y <= 2; y++) near.Add(point + new P2Int(x, y));
                }
            }
            _worlds[worldspace] = world;
        }
    }

    public bool IsNearBorderRegion(FormKey worldspace, P2Int point) =>
        _worlds.TryGetValue(worldspace, out var world) && world.NearBorder.Contains(point);

    public LandscapeEdges? GetEdges(FormKey worldspace, P2Int point) =>
        _worlds.TryGetValue(worldspace, out var world) ? world.Edges.GetValueOrDefault(point) : null;

    /// <summary>As <see cref="CellExtensions.IsInBorderRegion"/>, with whether each worldspace has a border region found once.</summary>
    private static bool IsInBorderRegion(ICellGetter cell, ILinkCache linkCache, ILinkUsageCache usageCache,
        System.Collections.Concurrent.ConcurrentDictionary<FormKey, bool> hasBorder)
    {
        if (cell.Regions != null && cell.Regions.Any(r => r.TryResolve(linkCache, out var region) && region.MajorFlags.HasFlag(Region.MajorFlag.BorderRegion)))
            return true;
        var world = cell.GetWorldspace(linkCache);
        if (world == null) return true;
        return !hasBorder.GetOrAdd(world.FormKey, static (_, a) => a.usageCache.GetUsagesOf<IRegionGetter>(a.world).UsageLinks
            .Select(r => r.Resolve(a.linkCache))
            .Any(r => r.MajorFlags.HasFlag(Region.MajorFlag.BorderRegion)), (usageCache, world, linkCache));
    }
}