using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using System.Runtime.ExceptionServices;
using Antigen.Skyrim.Caches;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Record.Landscape;

public class LandscapeSeamAnalyzer : IContextualRecordAnalyzer<ILandscapeGetter>
{
    public enum Direction
    {
        North,
        East,
        South,
        West
    };

    public static readonly TopicDefinition<Direction> HeightMapSeam = MutagenTopicBuilder.FromDiscussion(
            606,
            "Landscape height map seam",
            Severity.Error)
        .WithFormatting<Direction>("Landscape heightmap has seam in direction {0}");

    public static readonly TopicDefinition<Direction> VertexColorSeam = MutagenTopicBuilder.FromDiscussion(
            609,
            "Landscape vertex color seam",
            Severity.Warning)
        .WithFormatting<Direction>("Landscape vertex colors have seam in direction {0}");

    public static readonly TopicDefinition<Quadrant, Direction, IFormLinkGetter<ILandscapeTextureGetter>> TextureSeam = MutagenTopicBuilder.FromDiscussion(
            633,
            "Landscape texture seam",
            Severity.Warning)
        .WithFormatting<Quadrant, Direction, IFormLinkGetter<ILandscapeTextureGetter>>("Landscape quadrant {0} has texture seam in direction {1} with texture {2}");

    internal static readonly IReadOnlyArray2d<P3UInt8> DefaultVertexColors = new Array2d<P3UInt8>(new P2Int(LandscapeExtensions.GridSize, LandscapeExtensions.GridSize), new P3UInt8(255, 255, 255));
    // Minimum opacity difference to raise DefaultVertexColors. Somewhat arbritrary based on floating point errors + min difference for perception
    static readonly float AlphaOpacityEpsilon = 1.0f / 8.0f;

    public IEnumerable<TopicDefinition> Topics => [HeightMapSeam, VertexColorSeam, TextureSeam];

    static P2Int ToOffset(Direction direction)
    {
        return direction switch
        {
            Direction.North => new P2Int(0, 1),
            Direction.East => new P2Int(1, 0),
            Direction.South => new P2Int(0, -1),
            Direction.West => new P2Int(-1, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
    }
    static Direction Opposite(Direction direction)
    {
        return direction switch
        {
            Direction.North => Direction.South,
            Direction.East => Direction.West,
            Direction.South => Direction.North,
            Direction.West => Direction.East,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
    }

    /// <summary>The directions in order of their values, for indexing edges by direction.</summary>
    internal static readonly Direction[] Directions = [Direction.North, Direction.East, Direction.South, Direction.West];

    internal static IEnumerable<T> GetEdge<T>(IReadOnlyArray2d<T> data, Direction direction)
    {
        return direction switch
        {
            Direction.North => data.GetRow(data.Height - 1),
            Direction.East => data.GetColumn(data.Width - 1),
            Direction.South => data.GetRow(0),
            Direction.West => data.GetColumn(0),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
    }

    public readonly struct Difference<T>
    {
        public readonly int Index { get; init; }
        public readonly T Self { get; init;  }
        public readonly T Other { get; init; }

        public override string ToString()
        {
            return $"At {Index}: ({Self}) vs ({Other})";
        }

        public static IEnumerable<Difference<T>> GetDifferences(IEnumerable<T> self, IEnumerable<T> other, Func<T, T, bool> diffPredicate)
        {
            return self.Zip(other)
                .Select((pair, index) => new Difference<T>() { Index = index, Self = pair.First, Other = pair.Second })
                .Where(d => diffPredicate(d.Self, d.Other));
        }
    }

    public void AnalyzeRecord(ContextualRecordAnalyzerParams<ILandscapeGetter> param)
    {
        var landscape = param.Record;

        var cell = landscape.GetCell(param.LinkCache);
        var worldspace = cell?.GetWorldspace(param.LinkCache);
        if (cell?.Grid == null || worldspace == null) return;

        // Its neighbours' edges, and whether it's near a border region, are worked out once for the load order.
        var seams = param.ResolveCache<ILandscapeSeamCache>();
        var point = cell.Grid.Point;
        if (!seams.IsNearBorderRegion(worldspace.FormKey, point))
            return;

        // Its own edges are the cache's when it's the version that wins, and decoded from it otherwise.
        var cached = seams.GetEdges(worldspace.FormKey, point);
        var self = cached is { Failed: null } && cached.Landscape == landscape.FormKey && cached.Plugin == param.ModKey
            ? cached
            : LandscapeEdges.Of(landscape, param.ModKey);

        LandscapeEdges? GetNeighbour(Direction direction)
        {
            var edges = seams.GetEdges(worldspace.FormKey, point + ToOffset(direction));
            if (edges?.Failed is { } failed) ExceptionDispatchInfo.Throw(failed);
            return edges;
        }

        void CheckSeams<T>(TopicDefinition<Direction> topic, T[]?[]? data, Func<LandscapeEdges, T[]?[]?> getData)
            where T : IEquatable<T>
        {
            if (data == null)
                return;

            foreach (var dir in Directions)
            {
                var neighbour = GetNeighbour(dir);
                if (neighbour == null) continue;
                var neighbourData = getData(neighbour);
                if (neighbourData == null) continue;

                var diff = GetDifferences(data[(int)dir]!, neighbourData[(int)Opposite(dir)]!, static (a, b) => !a.Equals(b));
                if (diff.Length > 0)
                    param.AddTopic(topic.Format(dir), ("Differences", diff));
            }
        }

        CheckSeams(HeightMapSeam, self.Heights, l => l.Heights);
        CheckSeams(VertexColorSeam, self.Colors, l => l.Colors);

        void CheckTextures(QuadrantEdges selfQuadrant, QuadrantEdges otherQuadrant, Direction selfToOther)
        {
            foreach (var texture in selfQuadrant.Textures.And(otherQuadrant.Textures).Distinct())
            {
                var edgeSelf = selfQuadrant.Edge(texture, selfToOther);
                var edgeOther = otherQuadrant.Edge(texture, Opposite(selfToOther));

                // We need an epsilon here since opacities are stored as floats
                var diff = GetDifferences(edgeSelf, edgeOther, static (a, b) => !a.EqualsWithin(b, AlphaOpacityEpsilon));
                if (diff.Length > 0)
                    param.AddTopic(TextureSeam.Format(selfQuadrant.Quadrant, selfToOther, texture), ("Differences", diff));
            }
        }

        static QuadrantEdges Of(LandscapeEdges edges, Quadrant quadrant) => edges.Quadrants.First(q => q.Quadrant == quadrant);

        // Landscape textures ar broken into four quadrants per cell
        // This analysers checks are described as:
        // Where `[bt][lr]` defines a quadrant, and `[NESW]` defines a neighbouring cell
        //     | Nbl | Nbr |
        // Wtr | tl  | tr  | Etl
        //     +-----+-----+
        // Wbr | bl  | br  | Ebl
        //     | Stl | Str |

        var tl = Of(self, Quadrant.TopLeft);
        var tr = Of(self, Quadrant.TopRight);
        var bl = Of(self, Quadrant.BottomLeft);
        var br = Of(self, Quadrant.BottomRight);

        CheckTextures(tl, tr, Direction.East);
        CheckTextures(tl, bl, Direction.South);
        CheckTextures(bl, br, Direction.East);
        CheckTextures(tr, br, Direction.South);

        if (GetNeighbour(Direction.North) is { } north)
        {
            CheckTextures(tl, Of(north, Quadrant.BottomLeft), Direction.North);
            CheckTextures(tr, Of(north, Quadrant.BottomRight), Direction.North);
        }

        if (GetNeighbour(Direction.East) is { } east)
        {
            CheckTextures(tr, Of(east, Quadrant.TopLeft), Direction.East);
            CheckTextures(br, Of(east, Quadrant.BottomLeft), Direction.East);
        }

        if (GetNeighbour(Direction.South) is { } south)
        {
            CheckTextures(bl, Of(south, Quadrant.TopLeft), Direction.South);
            CheckTextures(br, Of(south, Quadrant.TopRight), Direction.South);
        }

        if (GetNeighbour(Direction.West) is { } west)
        {
            CheckTextures(tl, Of(west, Quadrant.TopRight), Direction.West);
            CheckTextures(bl, Of(west, Quadrant.BottomRight), Direction.West);
        }
    }

    /// <summary>Where two edges differ, as a list made now: a report's metadata is read later, on another thread.</summary>
    static Difference<T>[] GetDifferences<T>(T[] self, T[] other, Func<T, T, bool> diffPredicate)
    {
        List<Difference<T>>? found = null;
        for (var i = 0; i < Math.Min(self.Length, other.Length); i++)
        {
            if (diffPredicate(self[i], other[i])) (found ??= []).Add(new Difference<T> { Index = i, Self = self[i], Other = other[i] });
        }
        return found?.ToArray() ?? [];
    }

    public IEnumerable<Func<ILandscapeGetter, object?>> FieldsOfInterest()
    {
        yield return x => x.VertexHeightMap;
    }
}
