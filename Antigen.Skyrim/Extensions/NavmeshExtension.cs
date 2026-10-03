using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Extensions;

public static class NavmeshExtension
{
    public static bool TryGetTriangleArea(this INavigationMeshDataGetter navmeshData, int triangleIndex, out float area)
    {
        if (triangleIndex < 0 || triangleIndex >= navmeshData.Triangles.Count)
        {
            area = -1;
            return false;
        }

        var triangle = navmeshData.Triangles[triangleIndex];

        return TryGetTriangleArea(navmeshData, triangle, out area);
    }

    public static bool TryGetTriangleArea(this INavigationMeshDataGetter navmeshData, INavmeshTriangleGetter triangle, out float area) =>
        navmeshData.Vertices.TryGetTriangleArea(triangle, out area);

    /// <summary>
    /// As <see cref="TryGetTriangleArea(INavigationMeshDataGetter, INavmeshTriangleGetter, out float)"/>, of a navmesh's
    /// vertices read once: each read of <see cref="INavigationMeshDataGetter.Vertices"/> of a navmesh read from a plugin
    /// makes a new list.
    /// </summary>
    public static bool TryGetTriangleArea(this IReadOnlyList<P3Float> vertices, INavmeshTriangleGetter triangle, out float area)
    {
        var vertexX = vertices[triangle.Vertices.X];
        var vertexY = vertices[triangle.Vertices.Y];
        var vertexZ = vertices[triangle.Vertices.Z];

        var edgeA = vertexY - vertexX;
        var edgeB = vertexZ - vertexX;

        // Area of triangle = 0.5 * |edgeA x edgeB|
        area = 0.5f * edgeA.Cross(edgeB).Length;
        return true;
    }

    public static bool TryGetTriangleNormal(this INavigationMeshDataGetter navmeshData, int triangleIndex, out P3Float normal)
    {
        if (triangleIndex < 0 || triangleIndex >= navmeshData.Triangles.Count)
        {
            normal = default;
            return false;
        }

        var triangle = navmeshData.Triangles[triangleIndex];

        return TryGetTriangleNormal(navmeshData, triangle, out normal);
    }

    public static bool TryGetTriangleNormal(this INavigationMeshDataGetter navmeshData, INavmeshTriangleGetter triangle, out P3Float normal) =>
        navmeshData.Vertices.TryGetTriangleNormal(triangle, out normal);

    /// <summary>As <see cref="TryGetTriangleNormal(INavigationMeshDataGetter, int, out P3Float)"/>, of a navmesh's triangles and vertices read once.</summary>
    public static bool TryGetTriangleNormal(this IReadOnlyList<P3Float> vertices, IReadOnlyList<INavmeshTriangleGetter> triangles, int triangleIndex, out P3Float normal)
    {
        if (triangleIndex < 0 || triangleIndex >= triangles.Count)
        {
            normal = default;
            return false;
        }

        return vertices.TryGetTriangleNormal(triangles[triangleIndex], out normal);
    }

    /// <summary>As <see cref="TryGetTriangleNormal(INavigationMeshDataGetter, INavmeshTriangleGetter, out P3Float)"/>, of a navmesh's vertices read once.</summary>
    public static bool TryGetTriangleNormal(this IReadOnlyList<P3Float> vertices, INavmeshTriangleGetter triangle, out P3Float normal)
    {
        var vertexX = vertices[triangle.Vertices.X];
        var vertexY = vertices[triangle.Vertices.Y];
        var vertexZ = vertices[triangle.Vertices.Z];

        var edgeA = vertexY - vertexX;
        var edgeB = vertexZ - vertexX;
        normal = edgeA.Cross(edgeB).Normalize();
        return true;
    }
}
