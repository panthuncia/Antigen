using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Antigen.SDK.Caches;
using Antigen.Skyrim.Caches;
using Antigen.Skyrim.Util;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Record.LeveledNpc;

public class CircularLeveledNpcListAnalyzer : IContextualRecordAnalyzer<ILeveledNpcGetter>, IUsesCaches
{
    public static readonly TopicDefinition CircularLeveledNpc = MutagenTopicBuilder.FromDiscussion(
            242,
            "Circular Leveled Npc",
            Severity.Suggestion)
        .WithoutFormatting("Leveled Npc contains itself");

    public IEnumerable<Type> Caches => [typeof(ILeveledListCache)];

    public IEnumerable<TopicDefinition> Topics { get; } = [CircularLeveledNpc];

    public void AnalyzeRecord(ContextualRecordAnalyzerParams<ILeveledNpcGetter> param)
    {
        CircularLeveledListAnalyzerUtil.FindCircularList(param, l =>
        {
            if (l.Entries is not null)
            {
                return l.Entries
                    .Select(x => x.Data)
                    .WhereNotNull()
                    .Select(x => x.Reference.FormKey);
            }

            return [];
        }, CircularLeveledNpc, param.ResolveCache<ILeveledListCache>());
    }

    IEnumerable<Func<ILeveledNpcGetter, object?>> IContextualRecordAnalyzer<ILeveledNpcGetter>.FieldsOfInterest()
    {
        yield return x => x.Entries;
    }
}
