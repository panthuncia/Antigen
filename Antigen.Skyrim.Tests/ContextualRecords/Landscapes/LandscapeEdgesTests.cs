using Antigen.Skyrim.Caches;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests.ContextualRecords.Landscapes;

using Quadrant = Mutagen.Bethesda.Plugins.Records.Quadrant;

/// <summary>
/// A landscape's edges read from its layers' points are those read from its decoded grids: on the seam tests' plugin,
/// and on random layers (points set twice, opacities summing past one, layers without data, base layers).
/// </summary>
public class LandscapeEdgesTests
{
    [Fact]
    public void The_test_plugins_landscapes_have_the_same_edges()
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(LandscapeSeamAnalyzerTest.TestPlugin, SkyrimRelease.SkyrimSE);
        var landscapes = mod.EnumerateMajorRecords<ILandscapeGetter>().ToList();
        landscapes.ShouldNotBeEmpty();
        foreach (var landscape in landscapes) Same(landscape);
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 40)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_layers_have_the_same_edges(int seed)
    {
        var random = new Random(seed);
        var mod = new SkyrimMod(ModKey.FromFileName("Land.esp"), SkyrimRelease.SkyrimSE);
        var textures = Enumerable.Range(0, 4).Select(_ => mod.LandscapeTextures.AddNew().ToLink<ILandscapeTextureGetter>()).ToArray();
        var landscape = new Landscape(mod) { Layers = [] };
        for (var i = random.Next(0, 14); i > 0; i--)
        {
            var header = new LayerHeader
            {
                Quadrant = (Quadrant)random.Next(4),
                Texture = random.Next(5) == 0 ? new FormLink<ILandscapeTextureGetter>() : textures[random.Next(textures.Length)],
            };
            if (random.Next(4) == 0)
            {
                landscape.Layers.Add(new BaseLayer { Header = header });
                continue;
            }
            var alpha = new AlphaLayer { Header = header };
            if (random.Next(6) != 0)
            {
                alpha.AlphaLayerData = [];
                for (var p = random.Next(0, 120); p > 0; p--)
                {
                    // Edges often, and some points set twice.
                    var x = random.Next(3) == 0 ? (random.Next(2) == 0 ? 0 : 16) : random.Next(17);
                    var y = random.Next(3) == 0 ? (random.Next(2) == 0 ? 0 : 16) : random.Next(17);
                    var opacity = random.Next(5) == 0 ? 0f : random.Next(4) == 0 ? 1f : (float)random.NextDouble();
                    alpha.AlphaLayerData.Add(new AlphaLayerData { Position = (ushort)(x + (y * 17)), Opacity = opacity });
                }
            }
            landscape.Layers.Add(alpha);
        }
        Same(landscape);
    }

    private static void Same(ILandscapeGetter landscape)
    {
        var expected = LandscapeEdges.OfDecoded(landscape, landscape.FormKey.ModKey);
        var found = LandscapeEdges.Of(landscape, landscape.FormKey.ModKey);
        found.Quadrants.Length.ShouldBe(expected.Quadrants.Length);
        for (var q = 0; q < expected.Quadrants.Length; q++)
        {
            var (e, f) = (expected.Quadrants[q], found.Quadrants[q]);
            f.Quadrant.ShouldBe(e.Quadrant);
            f.Textures.Select(t => t.FormKey).ShouldBe(e.Textures.Select(t => t.FormKey));
            for (var l = 0; l < e.Opacity.Length; l++)
            {
                for (var d = 0; d < 4; d++)
                {
                    (f.Opacity[l][d] ?? new float[17]).ShouldBe(e.Opacity[l][d] ?? new float[17], $"quadrant {e.Quadrant}, layer {l}, direction {d}");
                }
            }
        }
    }
}