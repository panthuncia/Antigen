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
/// a reference whose base object isn't in the load order neither fails the search nor hides the cells after it. Analyzed
/// in parts, a cell each, on several threads at once, it reports what it reports analyzed whole.
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

    [Fact]
    public void Analyzed_in_parts_on_several_threads_it_reports_what_it_reports_whole()
    {
        var master = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var patch = new SkyrimMod(Patch, SkyrimRelease.SkyrimSE);
        var random = new Random(7);
        var statics = Enumerable.Range(0, 3).Select(i => master.Statics.AddNew($"Static{i}")).ToArray();
        for (var c = 0; c < 40; c++)
        {
            var cell = AddCell(master, $"Room{c}");
            var placed = Enumerable.Range(0, random.Next(0, 8)).Select(_ => Place(master, cell, statics[random.Next(3)].FormKey, x: random.Next(3))).ToList();
            if (placed.Count > 0 && random.Next(2) == 0)
            {
                var copy = (PlacedObject)placed[random.Next(placed.Count)].DeepCopy();
                copy.Placement!.Position = new(random.Next(3), 0, 0);
                AddCell(patch, cell).Persistent.Add(copy);
            }
        }
        var linkCache = new LoadOrder<IModListing<ISkyrimModGetter>>([new ModListing<ISkyrimModGetter>(master), new ModListing<ISkyrimModGetter>(patch)]).ToImmutableLinkCache();
        ContextualAnalyzerParams Params(Dropbox dropbox) =>
            new(linkCache, dropbox, new ProvideCaches(linkCache, TestCacheConstructors.All), new ReportContextParameters(linkCache));
        static string Describe(IEnumerable<(ModKey Mod, Topic Topic)> reports) =>
            string.Join("\n", reports.Select(r => $"{r.Mod} {r.Topic.FormattedTopic.FormattedMessage} {string.Join(";", r.Topic.MetaData.Select(m => $"{m.Name}={string.Join(",", ((IEnumerable<IPlacedObjectGetter>)m.Value!).Select(p => p.FormKey))}"))}").Order());

        var whole = new Dropbox();
        new DuplicateReferencesAnalyzer().Analyze(Params(whole));
        var analyzer = new DuplicateReferencesAnalyzer();
        var plan = analyzer.Plan(Params(new Dropbox()));
        var parts = new Dropbox[plan.Count];
        Parallel.For(0, plan.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            parts[i] = new Dropbox();
            plan.Analyze(Params(parts[i]), i, i + 1);
        });

        plan.Count.ShouldBe(40);
        whole.Reports.Count.ShouldBeGreaterThan(5);
        Describe(parts.SelectMany(p => p.Reports)).ShouldBe(Describe(whole.Reports));
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
