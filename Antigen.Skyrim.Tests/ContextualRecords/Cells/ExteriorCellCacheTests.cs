using Antigen.Skyrim.Caches;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests.ContextualRecords.Cells;

/// <summary>
/// The exterior cell cache made in parts, a version of a worldspace each, gives each worldspace's cell at each place as
/// going through the worldspace's versions in the order they resolve does (a later one listed over an earlier); on
/// random load orders of worldspaces whose versions add cells, and put other cells at places taken.
/// </summary>
public class ExteriorCellCacheTests
{
    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 20)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Made_in_parts_it_gives_each_place_s_cell_as_the_versions_list_them(int seed)
    {
        var random = new Random(seed);
        var mods = Enumerable.Range(0, 4).Select(i => new SkyrimMod(ModKey.FromFileName($"Mod{i}.esp"), SkyrimRelease.SkyrimSE)).ToArray();
        var worlds = Enumerable.Range(0, 3).Select(i => mods[0].Worldspaces.AddNew($"World{i}")).ToList();
        foreach (var mod in mods)
        {
            foreach (var world in worlds.Where(_ => random.Next(3) != 0))
            {
                var version = mod == mods[0] ? world : new Worldspace(world.FormKey, SkyrimRelease.SkyrimSE) { EditorID = world.EditorID };
                var cells = Enumerable.Range(0, random.Next(1, 12))
                    .Select(_ => new Cell(mod) { Grid = new CellGrid { Point = new P2Int(random.Next(-3, 3), random.Next(-3, 3)) } })
                    .ToList();
                version.SubCells.Add(new WorldspaceBlock
                {
                    BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock,
                    Items = [new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock, Items = [.. cells] }],
                });
                if (mod != mods[0]) mod.Worldspaces.Add(version);
            }
        }
        var linkCache = new LoadOrder<IModListing<ISkyrimModGetter>>(mods.Select(m => new ModListing<ISkyrimModGetter>(m))).ToImmutableLinkCache();

        var cache = new ImmutableExteriorCellCache(linkCache);

        foreach (var world in worlds)
        {
            var expected = new Dictionary<P2Int, FormKey>();
            foreach (var version in linkCache.ResolveAll<IWorldspaceGetter>(world.FormKey))
            {
                foreach (var cell in version.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items))
                {
                    if (cell.Grid is { } grid) expected[grid.Point] = cell.FormKey;
                }
            }
            for (var x = -4; x < 4; x++)
            {
                for (var y = -4; y < 4; y++)
                {
                    var point = new P2Int(x, y);
                    cache.GetExterior(world, point).FormKey.ShouldBe(expected.GetValueOrDefault(point, FormKey.Null), $"{world.EditorID} {point}");
                }
            }
        }
        cache.GetExterior(new FormLink<IWorldspaceGetter>(FormKey.Factory("000801:Nowhere.esp")), new P2Int(0, 0)).IsNull.ShouldBeTrue();
    }
}
