using Antigen.Skyrim.Caches;
using Antigen.Skyrim.Extensions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests.ContextualRecords.Cells;

/// <summary>
/// The cells' cache gives what reading the cells does: each cell's placed records in all its versions, and the exterior
/// doors reached from it through doors, on a load order of interiors joined by doors, one leading outside, and a patch
/// adding, moving and deleting references; asked about every cell, twice, and from several threads.
/// </summary>
public class CellPlacedCacheTests
{
    [Fact]
    public void The_cache_gives_what_reading_the_cells_does()
    {
        var master = new SkyrimMod(ModKey.FromFileName("Base.esm"), SkyrimRelease.SkyrimSE);
        var patch = new SkyrimMod(ModKey.FromFileName("Patch.esp"), SkyrimRelease.SkyrimSE);
        var door = master.Doors.AddNew("Door");
        var bucket = master.Statics.AddNew("Bucket");

        var hall = Interior(master, "Hall");
        var cellar = Interior(master, "Cellar");
        var attic = Interior(master, "Attic");
        var outside = new Cell(master) { EditorID = "Outside", Grid = new CellGrid { Point = new P2Int(0, 0) } };
        var world = master.Worldspaces.AddNew("World");
        world.SubCells.Add(new WorldspaceBlock
        {
            BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock,
            Items = [new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock, Items = [outside] }],
        });

        // Hall <-> Cellar <-> Attic, and Hall -> Outside.
        Link(master, door, hall, cellar);
        Link(master, door, cellar, attic);
        Link(master, door, hall, outside);
        var buckets = Enumerable.Range(0, 5).Select(i => Place(master, hall, bucket.FormKey, i)).ToList();

        // The patch adds a bucket, moves one and deletes another.
        var patched = Interior(patch, hall);
        Place(patch, patched, bucket.FormKey, 9);
        var moved = (PlacedObject)buckets[1].DeepCopy();
        moved.Placement!.Position = new(100, 0, 0);
        patched.Temporary.Add(moved);
        var deleted = (PlacedObject)buckets[2].DeepCopy();
        deleted.IsDeleted = true;
        patched.Temporary.Add(deleted);

        var linkCache = new LoadOrder<IModListing<ISkyrimModGetter>>([new ModListing<ISkyrimModGetter>(master), new ModListing<ISkyrimModGetter>(patch)]).ToImmutableLinkCache();
        var cache = new CellPlacedCache(linkCache);
        ICellGetter[] cells = [hall, cellar, attic, outside, patched];

        Parallel.For(0, 8, _ =>
        {
            foreach (var cell in cells.Concat(cells))
            {
                cache.Placed(cell).Select(p => p.FormKey).ShouldBe(cell.GetAllPlaced(linkCache).Select(p => p.FormKey));
                cache.ExteriorDoorsInto(cell).Select(d => d.FormKey).ShouldBe(cell.GetExteriorDoorsGoingIntoInteriorRecursively(linkCache).Select(d => d.FormKey));
            }
        });
        cache.Placed(hall).Count.ShouldBe(7);
        cache.ExteriorDoorsInto(attic).ShouldHaveSingleItem();
    }

    private static Cell Interior(SkyrimMod mod, string editorId) => Interior(mod, new Cell(mod) { EditorID = editorId, Flags = Cell.Flag.IsInteriorCell });

    private static Cell Interior(SkyrimMod mod, Cell cell)
    {
        var added = cell.FormKey.ModKey == mod.ModKey ? cell : new Cell(cell.FormKey, SkyrimRelease.SkyrimSE) { EditorID = cell.EditorID, Flags = cell.Flags };
        if (mod.Cells.Records.Count == 0)
        {
            mod.Cells.Records.Add(new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }] });
        }
        mod.Cells.Records[0].SubBlocks[0].Cells.Add(added);
        return added;
    }

    private static PlacedObject Place(SkyrimMod mod, Cell cell, FormKey baseObject, float x)
    {
        var placed = new PlacedObject(mod) { Base = new FormLinkNullable<IPlaceableObjectGetter>(baseObject), Placement = new Placement { Position = new(x, 0, 0) } };
        cell.Temporary.Add(placed);
        return placed;
    }

    /// <summary>A door in each cell, each leading to the other.</summary>
    private static void Link(SkyrimMod mod, Door door, Cell from, Cell to)
    {
        var there = Place(mod, to, door.FormKey, 1);
        var here = Place(mod, from, door.FormKey, 2);
        here.TeleportDestination = new TeleportDestination { Door = there.ToLink<IPlacedObjectGetter>() };
        there.TeleportDestination = new TeleportDestination { Door = here.ToLink<IPlacedObjectGetter>() };
    }
}
