using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Contextual;

public class DuplicateReferencesAnalyzer : IContextualAnalyzer
{
    public static readonly TopicDefinition<ICellGetter, IFormLinkNullableGetter<IPlaceableObjectGetter>> DuplicateReferences = MutagenTopicBuilder.FromDiscussion(
            207,
            "Duplicate References",
            Severity.Suggestion)
        .WithFormatting<ICellGetter, IFormLinkNullableGetter<IPlaceableObjectGetter>>("Cell {0} has multiple identical references of {1}");

    private static readonly FuncEqualityComparer<IPlacedObjectGetter> DuplicatePlacedComparer = new(
        (a, b) =>
        {
            if (a is null || b is null) return false;
            if (ReferenceEquals(a, b)) return true;

            return a.Placement!.Equals(b.Placement) && a.Scale.Equals(b.Scale) && a.Base.FormKey.Equals(b.Base.FormKey);
        },
        lookup => HashCode.Combine(lookup.Placement, lookup.Scale, lookup.Base.FormKey));

    public IEnumerable<TopicDefinition> Topics { get; } = [DuplicateReferences];

    public void Analyze(ContextualAnalyzerParams param)
    {
        // Each plugin's place in the load order, to blame a duplicate on the last plugin among its references' winners:
        // the one whose loading makes it. (Blaming the base object's winner blamed the wrong plugin, and threw when the
        // base isn't in the load order, ending the search over every cell after it.)
        var order = new Dictionary<ModKey, int>();
        foreach (var listing in param.LinkCache.ListedOrder)
        {
            order.TryAdd(listing.ModKey, order.Count);
        }

        foreach (var cell in param.LinkCache.PriorityOrder.WinningOverrides<ICellGetter>())
        {
            // Group all placed objects by their placement and scale
            var duplicateGroups = cell.GetAllPlaced(param.LinkCache)
                .Where(placed => placed is { IsDeleted: false, Placement: not null })
                .OfType<IPlacedObjectGetter>()
                .GroupBy(x => x, DuplicatePlacedComparer);

            foreach (var duplicateGroup in duplicateGroups)
            {
                var duplicates = duplicateGroup.ToArray();
                if (duplicates.Length <= 1) continue;

                // TODO: Exclude any placed objects with references to it
                var dispensableDuplicates = duplicates
                    .Where(placed => placed is { VirtualMachineAdapter: null, EnableParent: null, NavigationDoorLink: null, Patrol: null, LinkedReferences.Count: 0 })
                    .Where(placed => placed.SkyrimMajorRecordFlags.HasFlag((SkyrimMajorRecord.SkyrimMajorRecordFlag) PlacedObject.DefaultMajorFlag.Persistent))
                    .ToList();

                // All duplicates are indispensable
                if (dispensableDuplicates.Count == 0) continue;

                // Keep the first duplicate
                List<IPlacedObjectGetter> keptDuplicates;
                List<IPlacedObjectGetter> removedDuplicates;
                if (dispensableDuplicates.Count == duplicates.Length)
                {
                    keptDuplicates = [dispensableDuplicates[0]];
                    removedDuplicates = dispensableDuplicates.Skip(1).ToList();
                }
                else
                {
                    keptDuplicates = duplicates.Except(dispensableDuplicates).ToList();
                    removedDuplicates = dispensableDuplicates;
                }

                param.AddTopic(
                    MadeBy(param, order, duplicates, cell),
                    duplicateGroup.Key,
                    DuplicateReferences.Format(cell, duplicateGroup.Key.Base),
                    ("Keep", keptDuplicates),
                    ("Remove", removedDuplicates));
            }
        }
    }

    private static ModKey MadeBy(ContextualAnalyzerParams param, Dictionary<ModKey, int> order, IPlacedObjectGetter[] duplicates, ICellGetter cell)
    {
        ModKey? last = null;
        foreach (var placed in duplicates)
        {
            if (!param.LinkCache.TryResolveSimpleContext<IPlacedObjectGetter>(placed.FormKey, out var winner)) continue;
            if (last is null || order.GetValueOrDefault(winner.ModKey, -1) > order.GetValueOrDefault(last.Value, -1))
            {
                last = winner.ModKey;
            }
        }
        return last ?? cell.FormKey.ModKey;
    }
}
