using System.Collections.Concurrent;
using Antigen.SDK.Caches;
using Antigen.Skyrim.Extensions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace Antigen.Skyrim.Caches;

/// <summary>
/// What cells hold, for the checks reading it: a cell's placed records in all its versions
/// (<see cref="CellExtensions.GetAllPlaced"/>), and the exterior doors reached from an interior through its doors
/// (<see cref="CellExtensions.GetExteriorDoorsGoingIntoInteriorRecursively"/>). Both depend on the cell, not the version
/// checked, so each is worked out once rather than by every check of every version, each reading every reference anew.
/// </summary>
public interface ICellPlacedCache
{
    /// <summary>The cell's placed records that aren't deleted, as <see cref="CellExtensions.GetAllPlaced"/> gives them.</summary>
    IReadOnlyList<IPlacedGetter> Placed(ICellGetter cell);

    /// <summary>As <see cref="CellExtensions.GetExteriorDoorsGoingIntoInteriorRecursively"/> gives them.</summary>
    IReadOnlyList<IPlacedObjectGetter> ExteriorDoorsInto(ICellGetter cell);
}

public class CellPlacedCacheProvider : ICacheConstructor
{
    public Type CacheType => typeof(ICellPlacedCache);

    public object Construct(ILinkCache linkCache, IProvideCaches provideCaches) => new CellPlacedCache(linkCache);
}

/// <summary>
/// Made empty and filled as asked. A cell's placed records are many, so each thread keeps those of the last few cells it
/// asked about (a record's versions are checked one after another): each placed record read is large, and a thread
/// keeping many cells' keeps much of the load order. The doors reached are few, so all are kept, and so are where each
/// cell's doors lead: a walk through doors goes through a group of interiors from each of them, and reading each cell's
/// references again for every walk read the group's again for each of its cells.
/// </summary>
public sealed class CellPlacedCache(ILinkCache linkCache) : ICellPlacedCache
{
    private const int Kept = 8;
    private readonly ThreadLocal<Recent> _recent = new(static () => new Recent());
    private readonly ConcurrentDictionary<FormKey, IPlacedObjectGetter[]> _doors = new();
    private readonly ConcurrentDictionary<FormKey, DoorStep[]> _steps = new();

    public IReadOnlyList<IPlacedGetter> Placed(ICellGetter cell) => _recent.Value!.Placed(cell, linkCache);

    public IReadOnlyList<IPlacedObjectGetter> ExteriorDoorsInto(ICellGetter cell) =>
        _doors.TryGetValue(cell.FormKey, out var doors)
            ? doors
            : _doors.GetOrAdd(cell.FormKey, [.. Walk(cell)]);

    /// <summary>A door of a cell leading to another: the door it leads to, and that door's cell.</summary>
    private readonly record struct DoorStep(IPlacedObjectGetter Door, ICellGetter Cell);

    /// <summary>As <see cref="CellExtensions.GetExteriorDoorsGoingIntoInteriorRecursively"/> walks, by each cell's steps.</summary>
    private IEnumerable<IPlacedObjectGetter> Walk(ICellGetter cell)
    {
        HashSet<FormKey> visitedCells = [cell.FormKey];
        var queue = new Queue<ICellGetter>();
        queue.Enqueue(cell);

        while (queue.Count > 0)
        {
            foreach (var step in Steps(queue.Dequeue()))
            {
                if (step.Cell.IsInteriorCell())
                {
                    if (visitedCells.Add(step.Cell.FormKey)) queue.Enqueue(step.Cell);
                }
                else
                {
                    yield return step.Door;
                }
            }
        }
    }

    /// <summary>Where a cell's doors lead, in the order of its placed records: worked out once a cell.</summary>
    private DoorStep[] Steps(ICellGetter cell)
    {
        if (_steps.TryGetValue(cell.FormKey, out var steps)) return steps;
        var found = new List<DoorStep>();
        foreach (var placedObject in (_recent.Value!.KeptFor(cell) ?? cell.GetAllPlaced(linkCache)).OfType<IPlacedObjectGetter>())
        {
            // Has a teleport destination
            if (placedObject.TeleportDestination is not { Door.IsNull: false } destination) continue;

            // Teleport destination is a door
            if (!linkCache.TryResolve<IDoorGetter>(placedObject.Base.FormKey, out _)) continue;

            if (destination.Door.TryResolveSimpleContext(linkCache, out var destinationDoor)
                && destinationDoor.Parent?.Record is ICellGetter destinationCell)
            {
                found.Add(new DoorStep(destinationDoor.Record, destinationCell));
            }
        }
        return _steps.GetOrAdd(cell.FormKey, [.. found]);
    }

    private sealed class Recent
    {
        private readonly Dictionary<FormKey, IPlacedGetter[]> _placed = new();
        private readonly Queue<FormKey> _order = new();

        /// <summary>The cell's placed records, if this thread keeps them.</summary>
        public IPlacedGetter[]? KeptFor(ICellGetter cell) => _placed.GetValueOrDefault(cell.FormKey);

        public IPlacedGetter[] Placed(ICellGetter cell, ILinkCache linkCache)
        {
            if (_placed.TryGetValue(cell.FormKey, out var placed)) return placed;
            placed = [.. cell.GetAllPlaced(linkCache)];
            if (_order.Count == Kept) _placed.Remove(_order.Dequeue());
            _order.Enqueue(cell.FormKey);
            _placed[cell.FormKey] = placed;
            return placed;
        }
    }
}
