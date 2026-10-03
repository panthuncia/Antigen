using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Antigen.Skyrim.Caches;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace Antigen.Skyrim.Util;

public static class CircularLeveledListAnalyzerUtil
{
    public static void FindCircularList<T>(
        ContextualRecordAnalyzerParams<T> param,
        Func<T, IEnumerable<FormKey>> nestedEntriesSelector,
        TopicDefinition topic)
        where T : class, IMajorRecordGetter =>
        FindCircularList(param, nestedEntriesSelector, topic, null);

    /// <summary>
    /// Searches the lists a list holds for one holding itself, from the list's own version, reporting each list met again
    /// with the path to it. With the leveled lists' cache, a list from which nothing could be reported isn't searched: one
    /// reaching no cycle, nor the list the search started from. The reports are the same, without searching a list shared
    /// by many again for each.
    /// </summary>
    public static void FindCircularList<T>(
        ContextualRecordAnalyzerParams<T> param,
        Func<T, IEnumerable<FormKey>> nestedEntriesSelector,
        TopicDefinition topic,
        ILeveledListCache? cache)
        where T : class, IMajorRecordGetter
    {
        var stack = new Stack<T>();
        var root = param.Record.FormKey;
        // A list that isn't the winning version holds what the winning one may not: whether the rest reach it is asked.
        var rootWins = cache?.WinnerPlugin<T>(root) == param.ModKey;
        var reachesRoot = new Dictionary<FormKey, bool>();

        FindCircularListInternal(param.Record);

        void FindCircularListInternal(T t)
        {
            if (stack.Any(x => x.FormKey == t.FormKey))
            {
                param.AddTopic(topic.Format(), ("Path", stack.ToList()));
                return;
            }

            stack.Push(t);

            foreach (var formKey in nestedEntriesSelector(t))
            {
                if (cache is not null && CannotReport(formKey)) continue;
                if (!param.LinkCache.TryResolve<T>(formKey, out var leveledList)) continue;

                FindCircularListInternal(leveledList);
            }

            stack.Pop();
        }

        bool CannotReport(FormKey formKey)
        {
            if (stack.Any(x => x.FormKey == formKey) || cache!.MayReachCycle<T>(formKey)) return false;
            if (rootWins) return true;
            if (!reachesRoot.TryGetValue(formKey, out var reaches)) reachesRoot[formKey] = reaches = cache.Reaches<T>(formKey, root);
            return !reaches;
        }
    }
}
