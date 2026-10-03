using Antigen.SDK.Caches;
using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Mutagen.Bethesda.Skyrim;
using Antigen.Skyrim.Caches;

namespace Antigen.Skyrim.Record.Dialog.Responses;

public class SpeakerAnalyzer : IContextualRecordAnalyzer<IDialogResponsesGetter>, IUsesCaches
{
    public IEnumerable<Type> Caches => [typeof(ISpeakerCache)];

    public static readonly TopicDefinition MissingSpeaker = MutagenTopicBuilder.FromDiscussion(
            392,
            "Missing Speaker",
            Severity.Error)
        .WithoutFormatting("Dialog has no possible speaker based on its conditions and its quest's dialogue conditions");

    public static readonly TopicDefinition<IDialogResponsesGetter> DifferentSpeakerInSharedInfo = MutagenTopicBuilder.FromDiscussion(
            467,
            "Different Speaker in Shared Info",
            Severity.Error)
        .WithFormatting<IDialogResponsesGetter>(
            "Dialog uses a shared info {0} that has no speakers in common with itself");

    public IEnumerable<TopicDefinition> Topics { get; } = [MissingSpeaker, DifferentSpeakerInSharedInfo];

    public void AnalyzeRecord(ContextualRecordAnalyzerParams<IDialogResponsesGetter> param)
    {
        var dialogResponses = param.Record;

        // Who can speak each response is worked out once for the load order's winning versions.
        var speakerCache = param.ResolveCache<ISpeakerCache>();
        var speakers = speakerCache.For(dialogResponses, param.ModKey);
        if (speakers.IsEmpty)
        {
            param.AddTopic(
                MissingSpeaker.Format());
        }

        if (!dialogResponses.ResponseData.IsNull)
        {
            var sharedInfo = dialogResponses.ResponseData.TryResolve(param.LinkCache);
            if (sharedInfo is null) return;

            var sharedInfoSpeakers = speakerCache.Get(sharedInfo.FormKey) ?? speakerCache.Of(sharedInfo);
            if (!speakers.Intersects(sharedInfoSpeakers))
            {
                param.AddTopic(
                    DifferentSpeakerInSharedInfo.Format(sharedInfo));
            }
        }
    }

    public IEnumerable<Func<IDialogResponsesGetter, object?>> FieldsOfInterest()
    {
        yield return x => x.Conditions;
    }
}
