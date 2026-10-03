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

public class LandscapeSeamCacheProvider : IPartitionedCacheConstructor, IUsesCaches
{
    public Type CacheType => typeof(ILandscapeSeamCache);

    public IEnumerable<Type> Caches => [typeof(ILinkUsageCache)];

    public object Construct(ILinkCache linkCache, IProvideCaches provideCaches) => Plan(linkCache, provideCaches).BuildAll();

    /// <summary>A part for each exterior cell of every worldspace.</summary>
    public ICachePlan Plan(ILinkCache linkCache, IProvideCaches provideCaches) =>
        new LandscapeSeamCache.Planned(linkCache, provideCaches.Resolve<ILinkUsageCache>());
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
    private static readonly Quadrant[] QuadrantOrder = Enum.GetValues<Quadrant>();

    /// <summary>
    /// A landscape's edges, its texture layers' read from their points rather than from each layer's decoded grid (a
    /// landscape has about twenty). The same as <see cref="OfDecoded"/>. Throws as decoding it does.
    /// </summary>
    public static LandscapeEdges Of(ILandscapeGetter landscape, ModKey plugin)
    {
        var heights = landscape.VertexHeightMap?.Decode();
        var colors = landscape.VertexColors ?? LandscapeSeamAnalyzer.DefaultVertexColors;
        var byQuadrant = new List<IBaseLayerGetter>[QuadrantOrder.Length];
        foreach (var layer in landscape.Layers)
        {
            // As DecodeQuadrant: a layer without a header is in no quadrant.
            if (layer.Header is not { } header) continue;
            var at = Array.IndexOf(QuadrantOrder, header.Quadrant);
            if (at >= 0) (byQuadrant[at] ??= []).Add(layer);
        }
        return new LandscapeEdges
        {
            Plugin = plugin,
            Landscape = landscape.FormKey,
            Heights = heights is null ? null : Edges(heights, static e => e),
            Colors = Edges(colors, static e => e),
            Quadrants = [.. QuadrantOrder.Select((q, i) => QuadrantEdges.Of(q, byQuadrant[i] ?? []))],
        };
    }

    /// <summary>A landscape's edges, read from its decoded grids (<see cref="LandscapeExtensions.QuadrantData"/>).</summary>
    public static LandscapeEdges OfDecoded(ILandscapeGetter landscape, ModKey plugin)
    {
        var heights = landscape.VertexHeightMap?.Decode();
        var colors = landscape.VertexColors ?? LandscapeSeamAnalyzer.DefaultVertexColors;
        return new LandscapeEdges
        {
            Plugin = plugin,
            Landscape = landscape.FormKey,
            Heights = heights is null ? null : Edges(heights, static e => e),
            Colors = Edges(colors, static e => e),
            Quadrants = [.. QuadrantOrder.Select(q => QuadrantEdges.Of(landscape.Layers.DecodeQuadrant(q)))],
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

    /// <summary>
    /// A quadrant's layers' edges, from their points: as <see cref="LandscapeExtensions.QuadrantData"/> decodes them (each
    /// alpha layer's opacity, then the base layer's, what the others leave), without a grid for each.
    /// </summary>
    public static QuadrantEdges Of(Quadrant quadrant, IEnumerable<IBaseLayerGetter> layers)
    {
        const int size = 17;
        var baseTexture = LandscapeExtensions.DefaultTexture;
        var textures = new List<IFormLinkGetter<ILandscapeTextureGetter>>();
        var opacity = new List<float[]?[]>();
        foreach (var layer in layers)
        {
            if (layer.Header == null)
                throw new ArgumentException("Layer header should not be null");
            var texture = layer.Header.Texture.IsNull ? LandscapeExtensions.DefaultTexture : layer.Header.Texture;
            if (layer is not IAlphaLayerGetter alpha)
            {
                baseTexture = texture;
                continue;
            }
            if (alpha.AlphaLayerData == null) continue;
            // By direction: north the top row (y = 16), east the right column (x = 16), south and west the first; a point
            // set twice keeps the last, as in the grid.
            var edges = new float[]?[4];
            foreach (var point in alpha.AlphaLayerData)
            {
                var (x, y) = (point.Position % size, point.Position / size);
                if (y >= size) throw new IndexOutOfRangeException($"Alpha layer point {point.Position} is outside its quadrant.");
                if (y == size - 1) (edges[(int)LandscapeSeamAnalyzer.Direction.North] ??= new float[size])[x] = point.Opacity;
                if (x == size - 1) (edges[(int)LandscapeSeamAnalyzer.Direction.East] ??= new float[size])[y] = point.Opacity;
                if (y == 0) (edges[(int)LandscapeSeamAnalyzer.Direction.South] ??= new float[size])[x] = point.Opacity;
                if (x == 0) (edges[(int)LandscapeSeamAnalyzer.Direction.West] ??= new float[size])[y] = point.Opacity;
            }
            for (var d = 0; d < edges.Length; d++)
            {
                if (edges[d] is { } edge && Array.TrueForAll(edge, static o => o == 0)) edges[d] = null;
            }
            textures.Add(texture);
            opacity.Add(edges);
        }
        // The base layer: what the alpha layers leave, summed as Enumerable.Sum sums floats (in a double).
        var baseEdges = new float[]?[4];
        for (var d = 0; d < baseEdges.Length; d++)
        {
            var edge = new float[size];
            for (var i = 0; i < size; i++)
            {
                var sum = 0d;
                foreach (var layer in opacity) sum += layer[d]?[i] ?? 0f;
                edge[i] = Math.Max(0.0f, 1.0f - (float)sum);
            }
            baseEdges[d] = Array.TrueForAll(edge, static o => o == 0) ? null : edge;
        }
        textures.Add(baseTexture);
        opacity.Add(baseEdges);
        return new QuadrantEdges { Quadrant = quadrant, Textures = textures, Opacity = [.. opacity] };
    }

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
/// Made from the winning versions, in a part for each exterior cell (<see cref="Planned"/>): each worldspace's exterior
/// cells, which of them are in a border region, and their landscapes' edges.
/// </summary>
public class LandscapeSeamCache : ILandscapeSeamCache
{
    private readonly Dictionary<FormKey, World> _worlds;

    private sealed record World(HashSet<P2Int> NearBorder, Dictionary<P2Int, LandscapeEdges> Edges);

    public LandscapeSeamCache(ILinkCache linkCache, ILinkUsageCache usageCache)
        : this(((LandscapeSeamCache)new Planned(linkCache, usageCache).BuildAll())._worlds)
    {
    }

    private LandscapeSeamCache(Dictionary<FormKey, World> worlds) => _worlds = worlds;

    public bool IsNearBorderRegion(FormKey worldspace, P2Int point) =>
        _worlds.TryGetValue(worldspace, out var world) && world.NearBorder.Contains(point);

    public LandscapeEdges? GetEdges(FormKey worldspace, P2Int point) =>
        _worlds.TryGetValue(worldspace, out var world) ? world.Edges.GetValueOrDefault(point) : null;

    /// <summary>The cache being made: its parts are the exterior cells of every worldspace.</summary>
    public sealed class Planned : ICachePlan
    {
        private readonly ILinkCache _linkCache;
        private readonly ILinkUsageCache _usageCache;
        private readonly (FormKey World, P2Int Point, IFormLinkGetter<ICellGetter> Cell)[] _exteriors;
        private readonly bool[] _inBorder;
        private readonly LandscapeEdges?[] _edges;

        // Whether a worldspace has any border region; asked of every cell in it, so found once for each.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<FormKey, bool> _hasBorder = new();

        public Planned(ILinkCache linkCache, ILinkUsageCache usageCache)
        {
            _linkCache = linkCache;
            _usageCache = usageCache;
            _exteriors =
            [
                .. linkCache.PriorityOrder.SelectMany(m => m.EnumerateMajorRecords<IWorldspaceGetter>())
                    .Select(w => w.FormKey)
                    .Distinct()
                    .SelectMany(w => ImmutableExteriorCellCache.CreateLookupForWorld(linkCache, w).Select(e => (w, e.Key, e.Value))),
            ];
            _inBorder = new bool[_exteriors.Length];
            _edges = new LandscapeEdges?[_exteriors.Length];
        }

        public int Count => _exteriors.Length;

        public void Build(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (!_exteriors[i].Cell.TryResolve(_linkCache, out var cell)) continue;
                _inBorder[i] = IsInBorderRegion(cell);
                if (cell.GetLandscape(_linkCache) is not { } landscape) continue;
                var plugin = _linkCache.TryResolveSimpleContext(landscape, out var context) ? context.ModKey : landscape.FormKey.ModKey;
                try
                {
                    _edges[i] = LandscapeEdges.Of(landscape, plugin);
                }
                catch (Exception e)
                {
                    _edges[i] = new LandscapeEdges { Plugin = plugin, Landscape = landscape.FormKey, Failed = e };
                }
            }
        }

        public object Seal()
        {
            var worlds = new Dictionary<FormKey, World>();
            for (var i = 0; i < _exteriors.Length; i++)
            {
                var (worldspace, point, _) = _exteriors[i];
                if (!worlds.TryGetValue(worldspace, out var world)) worlds[worldspace] = world = new World([], []);
                if (_edges[i] is { } found) world.Edges[point] = found;
                if (!_inBorder[i]) continue;
                for (var x = -2; x <= 2; x++)
                {
                    for (var y = -2; y <= 2; y++) world.NearBorder.Add(point + new P2Int(x, y));
                }
            }
            return new LandscapeSeamCache(worlds);
        }

        /// <summary>As <see cref="CellExtensions.IsInBorderRegion"/>, with whether each worldspace has a border region found once.</summary>
        private bool IsInBorderRegion(ICellGetter cell)
        {
            if (cell.Regions != null && cell.Regions.Any(r => r.TryResolve(_linkCache, out var region) && region.MajorFlags.HasFlag(Region.MajorFlag.BorderRegion)))
                return true;
            var world = cell.GetWorldspace(_linkCache);
            if (world == null) return true;
            return !_hasBorder.GetOrAdd(world.FormKey, static (_, a) => a.usageCache.GetUsagesOf<IRegionGetter>(a.world).UsageLinks
                .Select(r => r.Resolve(a.linkCache))
                .Any(r => r.MajorFlags.HasFlag(Region.MajorFlag.BorderRegion)), (usageCache: _usageCache, world, linkCache: _linkCache));
        }
    }
}
