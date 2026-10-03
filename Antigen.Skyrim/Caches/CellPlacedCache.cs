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
/// Made empty and filled as asked. A cell's placed records are many, so each thread keeps those of the last cells it
/// asked about (a record's versions are checked one after another, and a walk through doors comes back to the same
/// cells); the doors reached are few, so all are kept.
/// </summary>
public sealed class CellPlacedCache(ILinkCache linkCache) : ICellPlacedCache
{
    private const int Kept = 64;
    private readonly ThreadLocal<Recent> _recent = new(static () => new Recent());
    private readonly ConcurrentDictionary<FormKey, IPlacedObjectGetter[]> _doors = new();

    public IReadOnlyList<IPlacedGetter> Placed(ICellGetter cell) => _recent.Value!.Placed(cell, linkCache);

    public IReadOnlyList<IPlacedObjectGetter> ExteriorDoorsInto(ICellGetter cell) =>
        _doors.TryGetValue(cell.FormKey, out var doors)
            ? doors
            : _doors.GetOrAdd(cell.FormKey, [.. cell.GetExteriorDoorsGoingIntoInteriorRecursively(linkCache, this)]);

    private sealed class Recent
    {
        private readonly Dictionary<FormKey, IPlacedGetter[]> _placed = new();
        private readonly Queue<FormKey> _order = new();

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
