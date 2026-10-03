using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Antigen.SDK.Caches;
using Antigen.Skyrim.Caches;
using Antigen.Skyrim.Util;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Antigen.Skyrim.Record.LeveledSpell;

public class CircularLeveledSpellListAnalyzer : IContextualRecordAnalyzer<ILeveledSpellGetter>, IUsesCaches
{
    public static readonly TopicDefinition CircularLeveledSpell = MutagenTopicBuilder.FromDiscussion(
            231,
            "Circular Leveled Spell",
            Severity.CTD)
        .WithoutFormatting("Leveled Spell contains itself in path {0}");

    public IEnumerable<Type> Caches => [typeof(ILeveledListCache)];

    public IEnumerable<TopicDefinition> Topics { get; } = [CircularLeveledSpell];

    public void AnalyzeRecord(ContextualRecordAnalyzerParams<ILeveledSpellGetter> param)
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
        }, CircularLeveledSpell, param.ResolveCache<ILeveledListCache>());
    }
    public IEnumerable<Func<ILeveledSpellGetter, object?>> FieldsOfInterest()
    {
        yield return x => x.Entries;
    }
}
