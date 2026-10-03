using Antigen.Drivers;
using Antigen.SDK.Analyzers;
using Antigen.SDK.Caches;
using Antigen.SDK.Drops;
using Antigen.SDK.Topics;
using Antigen.Skyrim.Extensions;
using Antigen.Skyrim.Record.LeveledItem;
using Antigen.Skyrim.Record.Npc;
using Antigen.Testing;
using Antigen.Testing.Frameworks;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests.ContextualRecords.LeveledItems;

/// <summary>
/// The leveled lists' cache changes how the circular list and ammo analyzers search, not what they find: on random
/// load orders of shared lists, cycles, lists holding themselves and overrides, each version's reports are those of
/// the search without it (kept here as it was).
/// </summary>
public class LeveledListCacheTests
{
    private static readonly ModKey Base = ModKey.FromFileName("Base.esm");
    private static readonly ModKey Patch = ModKey.FromFileName("Patch.esp");

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 30)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Circular_lists_are_found_as_without_the_cache(int seed)
    {
        var (mods, lists, _, _) = Generate(seed, acyclic: false);
        var linkCache = LinkCache(mods);
        var caches = new ProvideCaches(linkCache, TestCacheConstructors.All);
        var analyzer = new CircularLeveledItemListAnalyzer();

        foreach (var mod in mods)
        {
            foreach (var list in mod.LeveledItems)
            {
                var expected = new TestDropoff();
                FindCircularListWithout(Params(linkCache, mod.ModKey, (ILeveledItemGetter)list, expected, caches));
                var found = new TestDropoff();
                analyzer.AnalyzeRecord(Params(linkCache, mod.ModKey, (ILeveledItemGetter)list, found, caches));

                Paths(found).ShouldBe(Paths(expected), $"{list.FormKey} in {mod.ModKey}");
            }
        }
        lists.ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Missing_ammo_is_found_as_without_the_cache(int seed)
    {
        var (mods, _, npcs, _) = Generate(seed, acyclic: true);
        var linkCache = LinkCache(mods);
        var caches = new ProvideCaches(linkCache, TestCacheConstructors.All);
        var analyzer = new HasAmmoAnalyzer();

        foreach (var npc in npcs)
        {
            var expected = new TestDropoff();
            HasAmmoWithout(Params(linkCache, Base, (INpcGetter)npc, expected, caches));
            var found = new TestDropoff();
            analyzer.AnalyzeRecord(Params(linkCache, Base, (INpcGetter)npc, found, caches));

            found.Reports.Select(r => r.FormattedTopic.FormattedMessage).ShouldBe(expected.Reports.Select(r => r.FormattedTopic.FormattedMessage));
        }
    }

    private static ContextualRecordAnalyzerParams<T> Params<T>(ILinkCache linkCache, ModKey plugin, T record, IReportDropbox dropbox, IProvideCaches caches)
        where T : IMajorRecordGetter =>
        new(linkCache, new LoadOrder<IModListingGetter<IModGetter>>(), plugin, record, dropbox, caches);

    private static ILinkCache LinkCache(IReadOnlyList<SkyrimMod> mods) =>
        new LoadOrder<IModListing<ISkyrimModGetter>>(mods.Select(m => new ModListing<ISkyrimModGetter>(m))).ToImmutableLinkCache();

    private static List<List<FormKey>> Paths(TestDropoff dropoff) =>
        [.. dropoff.Reports.Select(r => ((IEnumerable<ILeveledItemGetter>)r.MetaData.Single(m => m.Name == "Path").Value!).Select(l => l.FormKey).ToList())];

    /// <summary>
    /// A base plugin of leveled items holding each other, weapons (bows, crossbows, swords) and ammunition, NPCs carrying
    /// them, and a patch overriding some lists with other entries. Acyclic, a list only holds lists made before it.
    /// </summary>
    private static (List<SkyrimMod> Mods, List<LeveledItem> Lists, List<Npc> Npcs, Random Random) Generate(int seed, bool acyclic)
    {
        var random = new Random(seed);
        var mod = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var patch = new SkyrimMod(Patch, SkyrimRelease.SkyrimSE);
        var items = new List<IItemGetter>();
        foreach (var animation in new[] { WeaponAnimationType.Bow, WeaponAnimationType.Crossbow, WeaponAnimationType.OneHandSword })
        {
            var weapon = mod.Weapons.AddNew();
            weapon.Data = new WeaponData { AnimationType = animation };
            items.Add(weapon);
        }
        foreach (var flags in new[] { Ammunition.Flag.NonBolt, (Ammunition.Flag)0 })
        {
            var ammunition = mod.Ammunitions.AddNew();
            ammunition.Flags = flags;
            items.Add(ammunition);
        }
        var lists = Enumerable.Range(0, 24).Select(_ => mod.LeveledItems.AddNew()).ToList();

        void Fill(LeveledItem list, int at)
        {
            list.Entries = [];
            var count = random.Next(0, 5);
            for (var i = 0; i < count; i++)
            {
                IItemGetter item = random.Next(3) == 0 || (acyclic && at == 0)
                    ? items[random.Next(items.Count)]
                    : lists[acyclic ? random.Next(at) : random.Next(lists.Count)];
                list.Entries.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Reference = item.ToLink<IItemGetter>(), Count = 1, Level = 1 } });
            }
        }

        for (var i = 0; i < lists.Count; i++) Fill(lists[i], i);
        foreach (var i in Enumerable.Range(0, lists.Count).Where(_ => random.Next(3) == 0))
        {
            var overridden = patch.LeveledItems.GetOrAddAsOverride(lists[i]);
            Fill(overridden, i);
        }
        var npcs = Enumerable.Range(0, 40).Select(_ =>
        {
            var npc = mod.Npcs.AddNew();
            npc.Items = [];
            for (var i = random.Next(0, 4); i > 0; i--)
            {
                IItemGetter item = random.Next(2) == 0 ? items[random.Next(items.Count)] : lists[random.Next(lists.Count)];
                var entry = new ContainerEntry();
                entry.Item.Item.SetTo(item.ToLink<IItemGetter>());
                npc.Items.Add(entry);
            }
            return npc;
        }).ToList();
        return ([mod, patch], lists, npcs, random);
    }

    /// <summary>The circular list search as it was before the cache.</summary>
    private static void FindCircularListWithout(ContextualRecordAnalyzerParams<ILeveledItemGetter> param)
    {
        var stack = new Stack<ILeveledItemGetter>();
        Find(param.Record);

        void Find(ILeveledItemGetter list)
        {
            if (stack.Any(x => x.FormKey == list.FormKey))
            {
                param.AddTopic(CircularLeveledItemListAnalyzer.CircularLeveledItem.Format(), ("Path", stack.ToList()));
                return;
            }
            stack.Push(list);
            foreach (var formKey in list.Entries?.Select(e => e.Data).WhereNotNull().Select(d => d.Reference.FormKey) ?? [])
            {
                if (param.LinkCache.TryResolve<ILeveledItemGetter>(formKey, out var nested)) Find(nested);
            }
            stack.Pop();
        }
    }

    /// <summary>The missing ammo check as it was before the cache.</summary>
    private static void HasAmmoWithout(ContextualRecordAnalyzerParams<INpcGetter> param)
    {
        IWeaponGetter? crossbow = null, bow = null;
        IAmmunitionGetter? arrow = null, bolt = null;
        foreach (var entry in param.Record.Items ?? [])
        {
            var item = entry.Item.Item.TryResolve(param.LinkCache);
            var weapon = item?.FindItem<IWeaponGetter>(param.LinkCache, w => w.Data?.AnimationType is WeaponAnimationType.Bow or WeaponAnimationType.Crossbow);
            if (weapon?.Data?.AnimationType == WeaponAnimationType.Bow) bow = weapon;
            else if (weapon?.Data?.AnimationType == WeaponAnimationType.Crossbow) crossbow = weapon;
            var ammo = item?.FindItem<IAmmunitionGetter>(param.LinkCache, _ => true);
            if (ammo is null) continue;
            if (ammo.Flags.HasFlag(Ammunition.Flag.NonBolt)) arrow = ammo;
            else bolt = ammo;
        }
        if (crossbow is not null && bolt is null) param.AddTopic(HasAmmoAnalyzer.MissingAmmo.Format(crossbow, "bolts"));
        if (bow is not null && arrow is null) param.AddTopic(HasAmmoAnalyzer.MissingAmmo.Format(bow, "arrows"));
    }
}