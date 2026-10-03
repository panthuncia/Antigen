using Antigen.Drivers;
using Antigen.SDK.Analyzers;
using Antigen.SDK.Drops;
using Antigen.SDK.Topics;
using Antigen.Skyrim.Contextual;
using Antigen.Testing.Frameworks;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests.ContextualRecords.Cells;

/// <summary>
/// A duplicate reference is blamed on the last plugin among its references' winners, the one whose loading makes it, and
/// a reference whose base object isn't in the load order neither fails the search nor hides the cells after it.
/// </summary>
public class DuplicateReferencesAnalyzerTests
{
    private static readonly ModKey Base = ModKey.FromFileName("Base.esm");
    private static readonly ModKey Patch = ModKey.FromFileName("Patch.esp");
    private static readonly ModKey Missing = ModKey.FromFileName("Missing.esm");

    [Fact]
    public void A_duplicate_is_blamed_on_the_plugin_that_makes_it()
    {
        var master = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var patch = new SkyrimMod(Patch, SkyrimRelease.SkyrimSE);
        var bucket = master.Statics.AddNew("Bucket");
        var cell = AddCell(master, "Room");
        var kept = Place(master, cell, bucket.FormKey, x: 10);
        var moved = Place(master, cell, bucket.FormKey, x: 50);

        // The patch moves the second bucket onto the first.
        var overridden = AddCell(patch, cell);
        var copy = (PlacedObject)moved.DeepCopy();
        copy.Placement!.Position = kept.Placement!.Position;
        overridden.Persistent.Add(copy);

        var reports = Analyze(master, patch);

        reports.ShouldHaveSingleItem().Mod.ShouldBe(Patch);
    }

    [Fact]
    public void A_base_outside_the_load_order_is_no_failure_and_later_cells_are_searched()
    {
        var master = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var bucket = master.Statics.AddNew("Bucket");
        var first = AddCell(master, "First");
        var nowhere = new FormKey(Missing, 0x800);
        Place(master, first, nowhere, x: 10);
        Place(master, first, nowhere, x: 10);
        var second = AddCell(master, "Second");
        Place(master, second, bucket.FormKey, x: 10);
        Place(master, second, bucket.FormKey, x: 10);

        var reports = Analyze(master);

        reports.Select(r => r.Mod).ShouldBe([Base, Base]);
    }

    private static List<(ModKey Mod, Topic Topic)> Analyze(params SkyrimMod[] mods)
    {
        var linkCache = new LoadOrder<IModListing<ISkyrimModGetter>>(mods.Select(m => new ModListing<ISkyrimModGetter>(m))).ToImmutableLinkCache();
        var dropbox = new Dropbox();
        new DuplicateReferencesAnalyzer().Analyze(new ContextualAnalyzerParams(
            linkCache, dropbox, new ProvideCaches(linkCache, TestCacheConstructors.All), new ReportContextParameters(linkCache)));
        return dropbox.Reports;
    }

    private static Cell AddCell(SkyrimMod mod, string editorId) => AddCell(mod, new Cell(mod) { EditorID = editorId, Flags = Cell.Flag.IsInteriorCell });

    private static Cell AddCell(SkyrimMod mod, Cell cell)
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
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(baseObject),
            Placement = new Placement { Position = new(x, 0, 0) },
        };
        placed.MajorRecordFlagsRaw |= (int)PlacedObject.DefaultMajorFlag.Persistent;
        cell.Persistent.Add(placed);
        return placed;
    }

    private sealed class Dropbox : IReportDropbox
    {
        public List<(ModKey Mod, Topic Topic)> Reports { get; } = [];

        public void Dropoff(ReportContextParameters parameters, ModKey mod, IFormLinkIdentifier record, Topic topic) => Reports.Add((mod, topic));

        public void Dropoff(ReportContextParameters parameters, Topic topic) => throw new InvalidOperationException("A report without a plugin");
    }
}
