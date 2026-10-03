using Antigen.Skyrim.Caches;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim.Records.Assets.VoiceType;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests.ContextualRecords.Dialog.Responses;

/// <summary>
/// Speaker sets held as voice types and named speakers answer as the speakers they stand for, listed one by one, would:
/// on random voice types (some sharing speakers, some with none, more than 64 of them) and random voices (whole voice
/// types, speakers named under voice types they have or not, FormKeys that aren't speakers, nothing).
/// </summary>
public class SpeakerSetTests
{
    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 40)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Sets_answer_as_their_speakers_listed(int seed)
    {
        var random = new Random(seed);
        var voiceTypes = Enumerable.Range(0, random.Next(1, 150)).Select(i => $"Voice{i}").ToList();
        var speakers = Enumerable.Range(0, random.Next(0, 300)).Select(i => new FormKey(ModKey.FromFileName("Speakers.esp"), (uint)(0x800 + i))).ToList();
        var voiceTypesOf = speakers.ToDictionary(s => s, _ => (IReadOnlyCollection<string>)voiceTypes.Where(_ => random.Next(voiceTypes.Count) < 2).ToList());
        var speakersOf = voiceTypes.ToDictionary(v => v, v => (IReadOnlyCollection<FormKey>)speakers.Where(s => voiceTypesOf[s].Contains(v)).ToList());
        var outsiders = Enumerable.Range(0, 5).Select(i => new FormKey(ModKey.FromFileName("Others.esp"), (uint)(0x800 + i))).ToList();
        var made = new SpeakerVoices(
            voiceTypes.Append("Unspoken"),
            v => speakersOf.TryGetValue(v, out var of) ? of : [],
            s => voiceTypesOf.TryGetValue(s, out var of) ? of : []);

        VoiceContainer? Voices()
        {
            if (random.Next(10) == 0) return null;
            var voices = new VoiceContainer();
            for (var i = random.Next(0, 4); i > 0; i--)
            {
                switch (random.Next(4))
                {
                    case 0:
                        voices.Insert(new VoiceContainer(Pick(voiceTypes.Append("Unspoken").ToList(), 3)));
                        break;
                    case 1 when speakers.Count > 0:
                        var speaker = speakers[random.Next(speakers.Count)];
                        voices.Insert(new VoiceContainer(speaker, voiceTypesOf[speaker]));
                        break;
                    case 2 when speakers.Count > 0:
                        voices.Insert(new VoiceContainer(speakers[random.Next(speakers.Count)], Pick(voiceTypes, 2)));
                        break;
                    default:
                        voices.Insert(new VoiceContainer(outsiders[random.Next(outsiders.Count)], Pick(voiceTypes, 1)));
                        break;
                }
            }
            return voices;
        }

        List<string> Pick(List<string> from, int most) => [.. Enumerable.Range(0, random.Next(1, most + 1)).Select(_ => from[random.Next(from.Count)]).Distinct()];

        HashSet<FormKey> Listed(VoiceContainer? voices) =>
        [
            .. (voices?.Voices ?? new Dictionary<string, HashSet<FormKey>>())
                .SelectMany(v => v.Value.Count > 0 ? v.Value : speakersOf.TryGetValue(v.Key, out var of) ? of : []),
        ];

        var all = Enumerable.Range(0, 60).Select(_ => Voices()).ToList();
        var sets = all.Select(made.Of).ToList();
        var listed = all.Select(Listed).ToList();
        for (var a = 0; a < all.Count; a++)
        {
            sets[a].IsEmpty.ShouldBe(listed[a].Count == 0, $"set {a}");
            for (var b = 0; b < all.Count; b++)
            {
                sets[a].Intersects(sets[b]).ShouldBe(listed[a].Overlaps(listed[b]), $"sets {a} and {b}");
            }
        }
    }
}
