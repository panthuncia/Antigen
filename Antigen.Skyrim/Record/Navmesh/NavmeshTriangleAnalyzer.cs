using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Mutagen.Bethesda.Skyrim;

namespace Antigen.Skyrim.Record.Navmesh;

public class NavmeshTriangleAnalyzer : IIsolatedRecordAnalyzer<INavigationMeshGetter>
{
    public static readonly TopicDefinition<int, int> TriangleNormal = MutagenTopicBuilder.FromDiscussion(
            400,
            "Linked triangles rotated in opposite directions",
            Severity.Warning)
        .WithFormatting<int, int>("Linked triangles {0} and {1} are rotated in opposite directions");

    public static readonly TopicDefinition<int, float> TriangleTooSmall = MutagenTopicBuilder.FromDiscussion(
            401,
            "Triangle is too small",
            Severity.Warning)
        .WithFormatting<int, float>("Triangle {0} has an area of {1}, which is too small");

    public IEnumerable<TopicDefinition> Topics => [TriangleNormal, TriangleTooSmall];

    public void AnalyzeRecord(IsolatedRecordAnalyzerParams<INavigationMeshGetter> param)
    {
        // Read once: each read of a navmesh's data, its triangles or its vertices makes them anew.
        if (param.Record.Data is not { } data) return;
        var triangles = data.Triangles;
        var vertices = data.Vertices;

        var alreadyCheckedTriangles = new HashSet<int>();
        for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            // Check neighbors
            var triangle = triangles[triangleIndex];
            if (!vertices.TryGetTriangleNormal(triangle, out var normal))
            {
                if (!triangle.Flags.HasFlag(NavmeshTriangle.Flag.EdgeLink_0_1))
                {
                    CheckNeighboringTriangle(triangle.EdgeLink_0_1);
                }

                if (!triangle.Flags.HasFlag(NavmeshTriangle.Flag.EdgeLink_1_2))
                {
                    CheckNeighboringTriangle(triangle.EdgeLink_1_2);
                }

                if (!triangle.Flags.HasFlag(NavmeshTriangle.Flag.EdgeLink_2_0))
                {
                    CheckNeighboringTriangle(triangle.EdgeLink_2_0);
                }

                void CheckNeighboringTriangle(short neighboringTriangleIndex)
                {
                    if (alreadyCheckedTriangles.Contains(neighboringTriangleIndex)) return;
                    if (float.IsNaN(normal.X)) return;
                    if (!vertices.TryGetTriangleNormal(triangles, neighboringTriangleIndex, out var neighboringNormal)) return;
                    if (float.IsNaN(neighboringNormal.X)) return;

                    var dot = normal.Dot(neighboringNormal);
                    if (dot > 0) return;
                    // if (Math.Abs(dot + 1) > 0.001) return;

                    param.AddTopic(
                        TriangleNormal.Format(triangleIndex, neighboringTriangleIndex));
                }
            }

            // Check triangle area
            if (vertices.TryGetTriangleArea(triangle, out var area) && area < 0.01f)
            {
                param.AddTopic(
                    TriangleTooSmall.Format(triangleIndex, area));
            }

            alreadyCheckedTriangles.Add(triangleIndex);
        }
    }

    public IEnumerable<Func<INavigationMeshGetter, object?>> FieldsOfInterest()
    {
        yield return x => x.Data?.Triangles;
        yield return x => x.Data?.Vertices;
    }
}
