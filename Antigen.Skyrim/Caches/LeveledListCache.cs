using Antigen.SDK.Caches;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Caches;

/// <summary>
/// The load order's leveled lists as graphs of their winning versions (items, NPCs and spells, each of its own kind),
/// worked out once rather than searched again from every list and every NPC carrying one: which lists can reach a cycle,
/// and the first ranged weapon and ammo a leveled item holds.
/// </summary>
public interface ILeveledListCache
{
    /// <summary>
    /// Whether a list of this kind can reach a list containing itself, following entries of the same kind through their
    /// winning versions: true for a list the cache doesn't know.
    /// </summary>
    bool MayReachCycle<T>(FormKey list) where T : class, IMajorRecordGetter;

    /// <summary>Whether a list reaches another (or is it), following entries through their winning versions.</summary>
    bool Reaches<T>(FormKey from, FormKey to) where T : class, IMajorRecordGetter;

    /// <summary>The plugin of a list's winning version; null for one the cache doesn't know.</summary>
    ModKey? WinnerPlugin<T>(FormKey list) where T : class, IMajorRecordGetter;

    /// <summary>
    /// The first bow or crossbow and the first ammunition in a leveled item, as
    /// <see cref="Extensions.ItemExtension.FindItem{T}"/> finds them in its winning version; false for one the cache
    /// doesn't know.
    /// </summary>
    bool TryGetRanged(FormKey leveledItem, out IWeaponGetter? rangedWeapon, out IAmmunitionGetter? ammunition);
}

public class LeveledListCacheProvider : IPartitionedCacheConstructor
{
    public Type CacheType => typeof(ILeveledListCache);

    public object Construct(ILinkCache linkCache, IProvideCaches provideCaches) => Plan(linkCache, provideCaches).BuildAll();

    /// <summary>A part for each leveled list.</summary>
    public ICachePlan Plan(ILinkCache linkCache, IProvideCaches provideCaches) => new LeveledListCache.Planned(linkCache);
}

/// <summary>Made in a part for each leveled list (<see cref="Planned"/>), from its winning version.</summary>
public class LeveledListCache : ILeveledListCache
{
    private readonly Dictionary<Type, Graph> _graphs;

    private LeveledListCache(Dictionary<Type, Graph> graphs) => _graphs = graphs;

    public bool MayReachCycle<T>(FormKey list) where T : class, IMajorRecordGetter =>
        !_graphs.TryGetValue(typeof(T), out var graph) || !graph.Index.TryGetValue(list, out var at) || graph.MayReachCycle[at];

    public bool Reaches<T>(FormKey from, FormKey to) where T : class, IMajorRecordGetter
    {
        if (from == to) return true;
        if (!_graphs.TryGetValue(typeof(T), out var graph) || !graph.Index.TryGetValue(from, out var start)) return false;
        if (!graph.Index.TryGetValue(to, out var target)) return false;
        var seen = new bool[graph.Keys.Length];
        var pending = new Stack<int>([start]);
        seen[start] = true;
        while (pending.TryPop(out var at))
        {
            foreach (var child in graph.Children[at])
            {
                if (child == target) return true;
                if (seen[child]) continue;
                seen[child] = true;
                pending.Push(child);
            }
        }
        return false;
    }

    public ModKey? WinnerPlugin<T>(FormKey list) where T : class, IMajorRecordGetter =>
        _graphs.TryGetValue(typeof(T), out var graph) && graph.Index.TryGetValue(list, out var at) ? graph.Plugins[at] : (ModKey?)null;

    public bool TryGetRanged(FormKey leveledItem, out IWeaponGetter? rangedWeapon, out IAmmunitionGetter? ammunition)
    {
        rangedWeapon = null;
        ammunition = null;
        var graph = _graphs[typeof(ILeveledItemGetter)];
        if (!graph.Index.TryGetValue(leveledItem, out var at) || graph.Ranged is null || !graph.RangedKnown[at]) return false;
        (rangedWeapon, ammunition) = graph.Ranged[at];
        return true;
    }

    /// <summary>Whether a weapon is one HasAmmo looks for: a bow or a crossbow.</summary>
    public static bool IsRanged(IWeaponGetter weapon) =>
        weapon.Data?.AnimationType is WeaponAnimationType.Bow or WeaponAnimationType.Crossbow;

    /// <summary>One kind of leveled list: its lists by FormKey, and each one's entries of the same kind.</summary>
    private sealed class Graph(FormKey[] keys)
    {
        public readonly FormKey[] Keys = keys;
        public readonly Dictionary<FormKey, int> Index = keys.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i);
        public readonly ModKey[] Plugins = new ModKey[keys.Length];
        public readonly bool[] Known = new bool[keys.Length];
        public readonly int[][] Children = new int[keys.Length][];
        public bool[] MayReachCycle = [];

        /// <summary>For leveled items: each one's entries, in order, as a list of the graph or the item itself.</summary>
        public Entry[][]? Entries;

        public (IWeaponGetter?, IAmmunitionGetter?)[]? Ranged;

        /// <summary>For leveled items: whether each one's <see cref="Ranged"/> is known.</summary>
        public bool[] RangedKnown = [];
    }

    private readonly record struct Entry(int List, IWeaponGetter? Weapon, IAmmunitionGetter? Ammunition, bool Unknown);

    /// <summary>The cache being made: its parts are the load order's leveled items, NPCs and spells.</summary>
    public sealed class Planned : ICachePlan
    {
        private readonly ILinkCache _linkCache;
        private readonly Graph _items;
        private readonly Graph _npcs;
        private readonly Graph _spells;

        public Planned(ILinkCache linkCache)
        {
            _linkCache = linkCache;
            _items = new Graph(Keys<ILeveledItemGetter>(linkCache));
            _items.Entries = new Entry[_items.Keys.Length][];
            _npcs = new Graph(Keys<ILeveledNpcGetter>(linkCache));
            _spells = new Graph(Keys<ILeveledSpellGetter>(linkCache));
        }

        private static FormKey[] Keys<T>(ILinkCache linkCache) where T : class, IMajorRecordGetter =>
            [.. linkCache.PriorityOrder.SelectMany(m => m.EnumerateMajorRecords<T>()).Select(r => r.FormKey).Distinct()];

        public int Count => _items.Keys.Length + _npcs.Keys.Length + _spells.Keys.Length;

        public void Build(int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (i < _items.Keys.Length)
                {
                    BuildItem(i);
                }
                else if (i < _items.Keys.Length + _npcs.Keys.Length)
                {
                    BuildList<ILeveledNpcGetter>(_npcs, i - _items.Keys.Length, l => l.Entries?.Select(e => e.Data).WhereNotNull().Select(d => d.Reference.FormKey));
                }
                else
                {
                    BuildList<ILeveledSpellGetter>(_spells, i - _items.Keys.Length - _npcs.Keys.Length, l => l.Entries?.Select(e => e.Data).WhereNotNull().Select(d => d.Reference.FormKey));
                }
            }
        }

        /// <summary>A list's winning version and its entries of the same kind, as the cycle analyzers follow them.</summary>
        private T? BuildList<T>(Graph graph, int at, Func<T, IEnumerable<FormKey>?> entries)
            where T : class, IMajorRecordGetter
        {
            if (!_linkCache.TryResolveSimpleContext<T>(graph.Keys[at], out var context))
            {
                graph.Children[at] = [];
                return null;
            }
            graph.Plugins[at] = context.ModKey;
            var children = new List<int>();
            foreach (var formKey in entries(context.Record) ?? [])
            {
                // An entry that isn't a list of this kind isn't followed; one that is, but the cache doesn't know (added
                // since its lists were listed), leaves the list unknown.
                if (graph.Index.TryGetValue(formKey, out var child)) children.Add(child);
                else if (_linkCache.TryResolve<T>(formKey, out _))
                {
                    graph.Children[at] = [];
                    return null;
                }
            }
            graph.Children[at] = [.. children];
            graph.Known[at] = true;
            return context.Record;
        }

        private void BuildItem(int at)
        {
            var list = BuildList<ILeveledItemGetter>(_items, at, l => l.Entries?.Select(e => e.Data).WhereNotNull().Select(d => d.Reference.FormKey));
            if (list is null)
            {
                _items.Entries![at] = [];
                return;
            }
            // As FindItem goes through them: each entry's item resolved, a leveled one searched in turn.
            var entries = new List<Entry>();
            foreach (var entry in list.Entries ?? [])
            {
                var item = entry.Data?.Reference.TryResolve(_linkCache);
                switch (item)
                {
                    case null:
                        continue;
                    case ILeveledItemGetter leveled:
                        entries.Add(_items.Index.TryGetValue(leveled.FormKey, out var child) ? new Entry(child, null, null, false) : new Entry(-1, null, null, true));
                        break;
                    default:
                        entries.Add(new Entry(-1, item is IWeaponGetter weapon && IsRanged(weapon) ? weapon : null, item as IAmmunitionGetter, false));
                        break;
                }
            }
            _items.Entries![at] = [.. entries];
        }

        public object Seal()
        {
            foreach (var graph in new[] { _items, _npcs, _spells }) graph.MayReachCycle = CyclesReachable(graph);
            _items.Ranged = Ranged(_items);
            return new LeveledListCache(new Dictionary<Type, Graph>
            {
                [typeof(ILeveledItemGetter)] = _items,
                [typeof(ILeveledNpcGetter)] = _npcs,
                [typeof(ILeveledSpellGetter)] = _spells,
            });
        }

        /// <summary>
        /// Which lists can reach a cycle: those in a strongly connected component with a cycle (more than one list, or one
        /// containing itself), or reaching one. Tarjan's algorithm, without recursion; it finds a component only after every
        /// component it reaches, so each is decided from those already found. An unknown list counts as reaching one.
        /// </summary>
        private static bool[] CyclesReachable(Graph graph)
        {
            var count = graph.Keys.Length;
            var index = new int[count];
            var low = new int[count];
            var onStack = new bool[count];
            var component = new int[count];
            Array.Fill(index, -1);
            var reaches = new List<bool>();
            var stack = new Stack<int>();
            var next = 0;
            var calls = new Stack<(int Node, int Child)>();
            for (var root = 0; root < count; root++)
            {
                if (index[root] >= 0) continue;
                calls.Push((root, 0));
                index[root] = low[root] = next++;
                stack.Push(root);
                onStack[root] = true;
                while (calls.Count > 0)
                {
                    var (node, child) = calls.Pop();
                    var children = graph.Children[node];
                    if (child < children.Length)
                    {
                        calls.Push((node, child + 1));
                        var to = children[child];
                        if (index[to] < 0)
                        {
                            index[to] = low[to] = next++;
                            stack.Push(to);
                            onStack[to] = true;
                            calls.Push((to, 0));
                        }
                        else if (onStack[to])
                        {
                            low[node] = Math.Min(low[node], index[to]);
                        }
                        continue;
                    }
                    if (calls.TryPeek(out var parent)) low[parent.Node] = Math.Min(low[parent.Node], low[node]);
                    if (low[node] != index[node]) continue;
                    // A component, found after every component it reaches.
                    var members = new List<int>();
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        component[member] = reaches.Count;
                        members.Add(member);
                    }
                    while (member != node);
                    var id = reaches.Count;
                    var cyclic = members.Count > 1 || graph.Children[node].Contains(node) || members.Any(m => !graph.Known[m]);
                    reaches.Add(cyclic || members.Any(m => graph.Children[m].Any(c => component[c] != id && reaches[component[c]])));
                }
            }
            return [.. Enumerable.Range(0, count).Select(i => reaches[component[i]])];
        }

        /// <summary>Each leveled item's first ranged weapon and ammunition, each list's found once from its entries'.</summary>
        private static (IWeaponGetter?, IAmmunitionGetter?)[] Ranged(Graph items)
        {
            var found = new (IWeaponGetter?, IAmmunitionGetter?)?[items.Keys.Length];
            var visiting = new bool[items.Keys.Length];
            // A list is known only if every list it holds is: one the cache doesn't know is searched as FindItem does.
            var known = (bool[])items.Known.Clone();

            (IWeaponGetter?, IAmmunitionGetter?) Of(int at)
            {
                if (found[at] is { } done) return done;
                // FindItem never ends on a list containing itself; here the list counts as holding nothing more.
                if (visiting[at]) return (null, null);
                visiting[at] = true;
                IWeaponGetter? weapon = null;
                IAmmunitionGetter? ammunition = null;
                foreach (var entry in items.Entries![at])
                {
                    if (entry.Unknown) known[at] = false;
                    var (entryWeapon, entryAmmunition) = entry.List >= 0 ? Of(entry.List) : (entry.Weapon, entry.Ammunition);
                    if (entry.List >= 0 && !known[entry.List]) known[at] = false;
                    weapon ??= entryWeapon;
                    ammunition ??= entryAmmunition;
                }
                visiting[at] = false;
                found[at] = (weapon, ammunition);
                return (weapon, ammunition);
            }

            var ranged = Enumerable.Range(0, items.Keys.Length).Select(Of).ToArray();
            items.RangedKnown = known;
            return ranged;
        }
    }
}