using Antigen.SDK.Analyzers;
using Antigen.SDK.Topics;
using Mutagen.Bethesda.Skyrim;

namespace Antigen.Skyrim.Record.Npc;

public sealed class RedundantPackageAnalyzer : IContextualRecordAnalyzer<INpcGetter>
{
    public static TopicDefinition<IPackageGetter> RedundantPackage = MutagenTopicBuilder.FromDiscussion(
            474,
            "Npc Uses Redundant Package",
            Severity.Warning)
        .WithFormatting<IPackageGetter>("Npc has package {0} which never plays as higher priority packages take priority at all times");

    public IEnumerable<TopicDefinition> Topics { get; } = [RedundantPackage];

    private readonly struct Time
    {
        public int Hour { get; init; }
        public int Minute { get; init; }
    }

    private readonly struct PackageTimeSpan
    {
        public IPackageGetter Package { get; init; }
        public Time Start { get; init; }
        public Time End { get; init; }
        public bool ExtendsToNextDay { get; init; }

        public bool IsSubsumedBy(PackageTimeSpan other)
        {
            if (ExtendsToNextDay)
            {
                if (other.ExtendsToNextDay)
                {
                    return Start.Hour >= other.Start.Hour && Start.Minute >= other.Start.Minute &&
                           End.Hour <= other.End.Hour && End.Minute <= other.End.Minute;
                }

                return false;
            }

            if (other.ExtendsToNextDay)
            {
                return Start.Hour >= other.Start.Hour && Start.Minute >= other.Start.Minute;
            }

            return Start.Hour >= other.Start.Hour && Start.Minute >= other.Start.Minute &&
                   End.Hour <= other.End.Hour && End.Minute <= other.End.Minute;
        }
    }

    public void AnalyzeRecord(ContextualRecordAnalyzerParams<INpcGetter> param)
    {
        var npc = param.Record;

        var timespans = new List<PackageTimeSpan>();
        foreach (var packageLink in npc.Packages)
        {
            var package = packageLink.TryResolve(param.LinkCache);
            if (package is null) continue;

            var hours = package.ScheduleHour == -1 ? Enumerable.Range(0, 24).ToArray() : [package.ScheduleHour];
            var minutes = package.ScheduleMinute == -1 ? package.ScheduleHour == -1 ? Enumerable.Range(0, 60).ToArray() : [0] : [package.ScheduleMinute];

            var packageTimespans = new List<PackageTimeSpan>();
            foreach (var hour in hours)
            {
                foreach (var minute in minutes)
                {
                    packageTimespans.Add(new PackageTimeSpan
                    {
                        Package = package,
                        Start = new Time
                        {
                            Hour = hour,
                            Minute = minute,
                        },
                        End = new Time
                        {
                            Hour = hour + (package.ScheduleDurationInMinutes - 1) / 60 % 24,
                            Minute = minute + (package.ScheduleDurationInMinutes - 1) % 60,
                        },
                        ExtendsToNextDay = (hour * 60 + minute + package.ScheduleDurationInMinutes - 1) / 60 >= 24
                    });
                }
            }

            // Check if the new package is subsumed by the existing packages
            if (Subsumed(packageTimespans, timespans, package.ScheduleDurationInMinutes))
            {
                param.AddTopic(
                    RedundantPackage.Format(package));
            }
            else
            {
                // Skip packages with conditions as they are not guaranteed to run all the time
                if (package.Conditions.Count > 0) continue;

                // Skip evaluation for packages that are not running every day
                if (package.ScheduleMonth != -1) continue;
                if (package.ScheduleDate != 0) continue;
                if (package.ScheduleDayOfWeek != (Mutagen.Bethesda.Skyrim.Package.DayOfWeek)255) continue;

                timespans.AddRange(packageTimespans);
            }
        }
    }

    /// <summary>
    /// Whether each of a package's spans is subsumed by one of those kept (<see cref="PackageTimeSpan.IsSubsumedBy"/>).
    /// All of a package's spans end as long after they start, so each span kept covers a rectangle of the hours and
    /// minutes this package's spans start at: these are marked on a grid of the day, once for the package, rather than
    /// each span compared with each kept (an NPC's packages running at any time are 1,440 spans each).
    /// </summary>
    private static bool Subsumed(List<PackageTimeSpan> spans, List<PackageTimeSpan> kept, int duration)
    {
        if (kept.Count == 0) return spans.Count == 0;

        var hours = (duration - 1) / 60 % 24;
        var minutes = (duration - 1) % 60;
        // A span going on into the next day is subsumed only by one that does too, with all four bounds; one that
        // doesn't, by one going on into the next day starting no later, or one that doesn't with all four bounds.
        var nextDay = new StartGrid();
        var sameDay = new StartGrid();
        foreach (var other in kept)
        {
            if (other.ExtendsToNextDay)
            {
                nextDay.Add(other.Start.Hour, other.End.Hour - hours, other.Start.Minute, other.End.Minute - minutes);
                sameDay.Add(other.Start.Hour, int.MaxValue, other.Start.Minute, int.MaxValue);
            }
            else
            {
                sameDay.Add(other.Start.Hour, other.End.Hour - hours, other.Start.Minute, other.End.Minute - minutes);
            }
        }
        nextDay.Seal();
        sameDay.Seal();

        foreach (var span in spans)
        {
            var subsumed = StartGrid.Holds(span.Start.Hour, span.Start.Minute)
                ? (span.ExtendsToNextDay ? nextDay : sameDay).Covers(span.Start.Hour, span.Start.Minute)
                : kept.Any(span.IsSubsumedBy);
            if (!subsumed) return false;
        }
        return true;
    }

    /// <summary>The starts of the day (hour and minute) covered by rectangles, counted with a two-dimensional prefix sum.</summary>
    private sealed class StartGrid
    {
        private const int Hours = 24;
        private const int Minutes = 60;
        private readonly int[,] _counts = new int[Hours + 1, Minutes + 1];

        public static bool Holds(int hour, int minute) => hour is >= 0 and < Hours && minute is >= 0 and < Minutes;

        /// <summary>Covers the starts from <paramref name="firstHour"/> to <paramref name="lastHour"/> and the minutes likewise, inclusive.</summary>
        public void Add(int firstHour, int lastHour, int firstMinute, int lastMinute)
        {
            firstHour = Math.Max(firstHour, 0);
            lastHour = Math.Min(lastHour, Hours - 1);
            firstMinute = Math.Max(firstMinute, 0);
            lastMinute = Math.Min(lastMinute, Minutes - 1);
            if (firstHour > lastHour || firstMinute > lastMinute) return;
            _counts[firstHour, firstMinute]++;
            _counts[firstHour, lastMinute + 1]--;
            _counts[lastHour + 1, firstMinute]--;
            _counts[lastHour + 1, lastMinute + 1]++;
        }

        public void Seal()
        {
            for (var h = 0; h <= Hours; h++)
            {
                for (var m = 0; m <= Minutes; m++)
                {
                    if (h > 0) _counts[h, m] += _counts[h - 1, m];
                    if (m > 0) _counts[h, m] += _counts[h, m - 1];
                    if (h > 0 && m > 0) _counts[h, m] -= _counts[h - 1, m - 1];
                }
            }
        }

        public bool Covers(int hour, int minute) => _counts[hour, minute] > 0;
    }

    public IEnumerable<Func<INpcGetter, object?>> FieldsOfInterest()
    {
        yield return x => x.Packages;
    }
}
