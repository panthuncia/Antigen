using Antigen.SDK.Caches;
using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Mutagen.Bethesda.Skyrim;
using Antigen.Skyrim.Caches;
using Mutagen.Bethesda.Plugins;
using Noggog;

namespace Antigen.Skyrim.Record.Dialog.Responses;

public class LinksToDifferentSpeakerAnalyzer : IContextualRecordAnalyzer<IDialogTopicGetter>, IUsesCaches
{
    public IEnumerable<Type> Caches => [typeof(ISpeakerCache)];

    public static readonly TopicDefinition<IDialogTopicGetter> LinksToDifferentSpeaker = MutagenTopicBuilder.FromDiscussion(
            468,
            "Links to Different Speaker",
            Severity.Error)
        .WithFormatting<IDialogTopicGetter>("Topic has response that links to topic {0} that has no speakers in common with the responses linking to it");

    public IEnumerable<TopicDefinition> Topics { get; } = [LinksToDifferentSpeaker];

    public void AnalyzeRecord(ContextualRecordAnalyzerParams<IDialogTopicGetter> param)
    {
        var dialogTopic = param.Record;
        // Who can speak each response is worked out once for the load order's winning versions.
        var speakerCache = param.ResolveCache<ISpeakerCache>();

        var topicsPerLink = dialogTopic.Responses
            .SelectMany(responses => responses.LinkTo.Select(link => (link, responses)))
            .GroupBy(x => x.link)
            .ToDictionary(g => g.Key, g => g.Select(x => x.responses).ToList());

        // The topic's own responses are of its version: the cache's only when that's the winning one.
        var speakersPerResponse = new Dictionary<FormKey, SpeakerSet>();
        foreach (var responses in dialogTopic.Responses)
        {
            if (!speakersPerResponse.ContainsKey(responses.FormKey))
                speakersPerResponse[responses.FormKey] = speakerCache.For(responses, param.ModKey);
        }

        foreach (var (linkTopicLink, responses) in topicsPerLink)
        {
            var linkTopic = linkTopicLink.TryResolve(param.LinkCache);
            if (linkTopic?.Responses is null) continue;

            var fromSpeakers = responses
                .Select(r => speakersPerResponse.GetValueOrDefault(r.FormKey))
                .WhereNotNull()
                .ToList();
            if (linkTopic.Responses
                .All(linkedResponses =>
                {
                    var linkedSpeakers = speakerCache.Get(linkedResponses.FormKey) ?? speakerCache.Of(linkedResponses);
                    return fromSpeakers.All(speakers => !speakers.Intersects(linkedSpeakers));
                }))
            {
                param.AddTopic(
                    LinksToDifferentSpeaker.Format(linkTopic),
                    ("Responses the link comes from", responses));
            }
        }
    }

    public IEnumerable<Func<IDialogTopicGetter, object?>> FieldsOfInterest()
    {
        yield return x => x.Responses;
    }
}
