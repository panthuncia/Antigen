using Antigen.Drivers;
using Antigen.SDK.Analyzers;
using Antigen.Skyrim.Record.Npc;
using Antigen.Testing;
using Antigen.Testing.Frameworks;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests.ContextualRecords.Npcs;

/// <summary>
/// Redundant packages are found, with starts marked on a grid, as they were by comparing each span with each
/// (<see cref="ReferenceRedundantPackageAnalyzer"/>): on random NPCs of packages running at any hour or minute, at
/// one, for any duration (none, over a day, below zero), at hours and minutes out of range, with conditions or not, and
/// not every day.
/// </summary>
public class RedundantPackageAnalyzerTests
{
    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 40)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Redundant_packages_are_found_as_by_comparing_each_span(int seed)
    {
        var random = new Random(seed);
        var mod = new SkyrimMod(ModKey.FromFileName("Packages.esp"), SkyrimRelease.SkyrimSE);
        var packages = Enumerable.Range(0, 16).Select(_ =>
        {
            var package = mod.Packages.AddNew();
            package.ScheduleHour = (sbyte)(random.Next(3) == 0 ? -1 : random.Next(8) == 0 ? random.Next(-5, 30) : random.Next(24));
            package.ScheduleMinute = (sbyte)(random.Next(3) == 0 ? -1 : random.Next(8) == 0 ? random.Next(-5, 70) : random.Next(60));
            package.ScheduleDurationInMinutes = random.Next(6) switch
            {
                0 => 0,
                1 => random.Next(-100, 0),
                2 => random.Next(1, 3000),
                3 => 60 * random.Next(1, 25),
                _ => random.Next(1, 300),
            };
            package.ScheduleMonth = (sbyte)(random.Next(6) == 0 ? random.Next(12) : -1);
            package.ScheduleDate = (byte)(random.Next(6) == 0 ? random.Next(1, 28) : 0);
            package.ScheduleDayOfWeek = random.Next(6) == 0 ? (Package.DayOfWeek)random.Next(7) : (Package.DayOfWeek)255;
            if (random.Next(5) == 0) package.Conditions.Add(new ConditionFloat { Data = new GetIsIDConditionData() });
            return package;
        }).ToList();
        var npcs = Enumerable.Range(0, 30).Select(_ =>
        {
            var npc = mod.Npcs.AddNew();
            for (var i = random.Next(0, 7); i > 0; i--) npc.Packages.Add(packages[random.Next(packages.Count)].ToLink<IPackageGetter>());
            return npc;
        }).ToList();
        var linkCache = new LoadOrder<IModListing<ISkyrimModGetter>>([new ModListing<ISkyrimModGetter>(mod)]).ToImmutableLinkCache();
        var caches = new ProvideCaches(linkCache, TestCacheConstructors.All);

        var reported = 0;
        foreach (var npc in npcs)
        {
            var expected = new TestDropoff();
            new ReferenceRedundantPackageAnalyzer().AnalyzeRecord(Params(linkCache, mod.ModKey, npc, expected, caches));
            var found = new TestDropoff();
            new RedundantPackageAnalyzer().AnalyzeRecord(Params(linkCache, mod.ModKey, npc, found, caches));

            found.Reports.Select(r => r.FormattedTopic.FormattedMessage).ShouldBe(expected.Reports.Select(r => r.FormattedTopic.FormattedMessage), $"{npc.FormKey}");
            reported += expected.Reports.Count;
        }
        reported.ShouldBeGreaterThan(0);
    }

    private static ContextualRecordAnalyzerParams<INpcGetter> Params(ILinkCache linkCache, ModKey plugin, INpcGetter record, TestDropoff dropbox, ProvideCaches caches) =>
        new(linkCache, new LoadOrder<IModListingGetter<IModGetter>>(), plugin, record, dropbox, caches);
}
