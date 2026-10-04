using System.Globalization;

namespace WmplWrap.Desktop;

internal enum DashboardGraphRange { PastWeek, PastMonth, PastYear, AllTime, Custom }
internal enum DashboardGraphMeasure { Listens, ListeningTime, TracksListened }
internal enum DashboardGraphGrouping { Total, Artist, Album, Track }
internal enum DashboardGraphGranularity { Auto, Day, Week, Month }
internal enum DashboardGraphMode { Activity, Cumulative, Breakdown }

internal sealed record DashboardGraphOptions(
    DashboardGraphRange Range,
    DashboardGraphMeasure Measure,
    DashboardGraphGrouping Grouping,
    DashboardGraphGranularity Granularity,
    DashboardGraphMode Mode,
    DateTime? CustomStart,
    DateTime? CustomEnd,
    bool IncludeBaseline,
    int TopSeriesLimit,
    bool CombineRemainingSeries);

internal sealed record DashboardGraph(string[] Labels, string[] Tooltips, DashboardGraphSeries[] Series, string Summary, string EmptyMessage);
internal sealed record DashboardGraphSeries(string Name, double[] Values, double[] IntervalValues, double?[] RatesPerDay);

internal static class DashboardGraphBuilder
{
    public static DashboardGraph Build(IReadOnlyList<LibrarySnapshot> snapshots, DashboardGraphOptions options)
    {
        if (snapshots.Count == 0)
            return Empty("Capture snapshots to begin charting listening activity.");

        var intervals = BuildIntervals(snapshots, options.IncludeBaseline);
        var latest = snapshots[^1].CapturedAtUtc;
        var (start, end) = ResolveRange(options, latest);
        intervals = intervals.Where(interval => interval.End >= start && interval.End <= end).ToList();

        if (intervals.Count == 0)
            return Empty(options.IncludeBaseline
                ? "No recorded listening data falls in this range."
                : "This range has no comparable snapshot intervals. Enable baseline inclusion or capture another snapshot.");

        var granularity = ResolveGranularity(options.Granularity, start, end);
        var entries = intervals
            .SelectMany(interval => interval.Rows.Select(row => new GraphEntry(
                BucketKey(interval.End, granularity),
                interval,
                GroupName(row.Track, options.Grouping),
                row.Track.Id,
                MetricValue(row, options.Measure))))
            .ToList();

        var buckets = intervals
            .GroupBy(interval => BucketKey(interval.End, granularity))
            .OrderBy(group => group.Key)
            .Select(group => new GraphBucket(
                group.Key,
                LabelForBucket(group.Key, granularity),
                TooltipForBucket(group.OrderBy(interval => interval.End).ToArray()),
                group.ToArray()))
            .ToArray();

        var categories = SelectCategories(entries, options);
        var series = categories.Select(category =>
        {
            var intervalValues = buckets.Select(bucket => ValueForBucket(entries, bucket, category, options)).ToArray();
            return new DashboardGraphSeries(
                category,
                intervalValues,
                intervalValues,
                buckets.Select(bucket => RateForBucket(entries, bucket, category, options)).ToArray());
        }).ToArray();

        if (options.Mode == DashboardGraphMode.Cumulative)
            series = series.Select(series => series with { Values = RunningTotal(series.Values) }).ToArray();

        var metric = options.Measure switch
        {
            DashboardGraphMeasure.ListeningTime => "listening time",
            DashboardGraphMeasure.TracksListened => "distinct tracks",
            _ => "listens"
        };
        var mode = options.Mode switch
        {
            DashboardGraphMode.Cumulative => "cumulative",
            DashboardGraphMode.Breakdown => "breakdown",
            _ => "activity"
        };
        var includesFirstSeenCounts = intervals.Any(interval => interval.Rows.Any(row => row.FirstSeenListens > 0));
        var summary = $"{intervals.Count:N0} recorded {(intervals.Count == 1 ? "interval" : "intervals")} · {mode} by {metric}";
        if (includesFirstSeenCounts) summary += " · includes first-seen WMP counts";
        return new DashboardGraph(buckets.Select(bucket => bucket.Label).ToArray(), buckets.Select(bucket => bucket.Tooltip).ToArray(), series, summary, "");
    }

    private static List<GraphInterval> BuildIntervals(IReadOnlyList<LibrarySnapshot> snapshots, bool includeBaseline)
    {
        var intervals = new List<GraphInterval>();
        if (includeBaseline)
        {
            var baselineRows = snapshots[0].Tracks
                .Where(track => track.PlayCount > 0)
                .Select(track => new GraphRow(track, track.PlayCount, track.PlayCount))
                .ToArray();
            if (baselineRows.Length > 0)
                intervals.Add(new GraphInterval(null, snapshots[0].CapturedAtUtc, true, baselineRows));
        }

        for (var index = 1; index < snapshots.Count; index++)
        {
            var report = Reporting.Compare(snapshots[index - 1], snapshots[index], includeBaseline);
            var rows = report.Rows
                .Where(row => row.Listens > 0)
                .Select(row => new GraphRow(row.Track, row.Listens, row.FirstSeenListens))
                .ToArray();
            intervals.Add(new GraphInterval(snapshots[index - 1].CapturedAtUtc, snapshots[index].CapturedAtUtc, false, rows));
        }

        return intervals;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ResolveRange(DashboardGraphOptions options, DateTimeOffset latest)
    {
        var latestLocal = ToEastern(latest);
        return options.Range switch
        {
            DashboardGraphRange.PastWeek => (latestLocal.AddDays(-7), latest),
            DashboardGraphRange.PastMonth => (latestLocal.AddMonths(-1), latest),
            DashboardGraphRange.PastYear => (latestLocal.AddYears(-1), latest),
            DashboardGraphRange.Custom when options.CustomStart is { } start && options.CustomEnd is { } end =>
                (new DateTimeOffset(start.Date, latestLocal.Offset), new DateTimeOffset(end.Date.AddDays(1).AddTicks(-1), latestLocal.Offset)),
            DashboardGraphRange.Custom when options.CustomStart is { } start =>
                (new DateTimeOffset(start.Date, latestLocal.Offset), latest),
            DashboardGraphRange.Custom when options.CustomEnd is { } end =>
                (DateTimeOffset.MinValue, new DateTimeOffset(end.Date.AddDays(1).AddTicks(-1), latestLocal.Offset)),
            _ => (DateTimeOffset.MinValue, latest)
        };
    }

    private static DashboardGraphGranularity ResolveGranularity(DashboardGraphGranularity requested, DateTimeOffset start, DateTimeOffset end)
    {
        if (requested != DashboardGraphGranularity.Auto) return requested;
        var days = (end - start).TotalDays;
        return days <= 45 ? DashboardGraphGranularity.Day
            : days <= 365 ? DashboardGraphGranularity.Week
            : DashboardGraphGranularity.Month;
    }

    private static DateTime BucketKey(DateTimeOffset end, DashboardGraphGranularity granularity)
    {
        var local = ToEastern(end).Date;
        return granularity switch
        {
            DashboardGraphGranularity.Week => local.AddDays(-((int)local.DayOfWeek + 6) % 7),
            DashboardGraphGranularity.Month => new DateTime(local.Year, local.Month, 1),
            _ => local
        };
    }

    private static string LabelForBucket(DateTime key, DashboardGraphGranularity granularity) => granularity switch
    {
        DashboardGraphGranularity.Month => key.ToString("MMM yyyy", CultureInfo.CurrentCulture),
        DashboardGraphGranularity.Week => key.ToString("MMM d", CultureInfo.CurrentCulture),
        _ => key.ToString("MMM d", CultureInfo.CurrentCulture)
    };

    private static string TooltipForBucket(IReadOnlyList<GraphInterval> intervals)
    {
        if (intervals.Count == 1 && intervals[0].IsBaseline)
            return $"Baseline captured {ToEastern(intervals[0].End):MMM d, yyyy h:mm tt}\nIncludes first-seen WMP counts";

        var first = intervals[0];
        var last = intervals[^1];
        var start = first.Start ?? first.End;
        var label = intervals.Count == 1
            ? $"Recorded {ToEastern(start):MMM d, yyyy h:mm tt} to {ToEastern(last.End):MMM d, yyyy h:mm tt}"
            : $"{intervals.Count:N0} snapshot intervals ending {ToEastern(first.End):MMM d} to {ToEastern(last.End):MMM d, yyyy}";
        var notes = new List<string>();
        if (intervals.Any(interval => interval.Rows.Any(row => row.FirstSeenListens > 0)))
            notes.Add("Includes first-seen WMP counts");
        var days = IntervalDays(intervals);
        if (days > 0) notes.Add($"Observed over: {days:N2} elapsed days");

        return notes.Count == 0 ? label : $"{label}\n{string.Join("\n", notes)}";
    }

    private static string[] SelectCategories(IReadOnlyList<GraphEntry> entries, DashboardGraphOptions options)
    {
        if (options.Grouping == DashboardGraphGrouping.Total) return ["All listening"];

        var ranked = entries
            .GroupBy(entry => entry.Category)
            .Select(group => new
            {
                Name = group.Key,
                Value = group.GroupBy(entry => entry.Bucket).Sum(bucket => Aggregate(bucket, options.Measure))
            })
            .OrderByDescending(group => group.Value)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(options.TopSeriesLimit <= 0 ? int.MaxValue : options.TopSeriesLimit)
            .Select(group => group.Name)
            .ToList();

        if (options.CombineRemainingSeries && entries.Any(entry => !ranked.Contains(entry.Category, StringComparer.Ordinal))) ranked.Add("Other");
        return ranked.ToArray();
    }

    private static double ValueForBucket(IReadOnlyList<GraphEntry> entries, GraphBucket bucket, string category, DashboardGraphOptions options)
    {
        var bucketEntries = EntriesForBucket(entries, bucket, category, options);
        return Aggregate(bucketEntries, options.Measure);
    }

    private static double? RateForBucket(IReadOnlyList<GraphEntry> entries, GraphBucket bucket, string category, DashboardGraphOptions options)
    {
        var comparableIntervals = bucket.Intervals.Where(interval => !interval.IsBaseline).ToArray();
        var days = IntervalDays(comparableIntervals);
        if (days <= 0) return null;

        var comparableEntries = EntriesForBucket(entries, bucket, category, options)
            .Where(entry => !entry.Interval.IsBaseline);
        return Aggregate(comparableEntries, options.Measure) / days;
    }

    private static IEnumerable<GraphEntry> EntriesForBucket(IReadOnlyList<GraphEntry> entries, GraphBucket bucket, string category, DashboardGraphOptions options)
    {
        var bucketEntries = entries.Where(entry => entry.Bucket == bucket.Key);
        if (options.Grouping != DashboardGraphGrouping.Total)
        {
            var ranked = SelectCategories(entries, options).Where(name => name != "Other").ToHashSet(StringComparer.Ordinal);
            bucketEntries = category == "Other"
                ? bucketEntries.Where(entry => !ranked.Contains(entry.Category))
                : bucketEntries.Where(entry => entry.Category == category);
        }
        return bucketEntries;
    }

    private static double IntervalDays(IEnumerable<GraphInterval> intervals) => intervals
        .Where(interval => interval.Start is not null)
        .Sum(interval => Math.Max(0, (interval.End - interval.Start!.Value).TotalDays));

    private static double Aggregate(IEnumerable<GraphEntry> entries, DashboardGraphMeasure measure) => measure == DashboardGraphMeasure.TracksListened
        ? entries.Select(entry => entry.TrackId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        : entries.Sum(entry => entry.Value);

    private static double[] RunningTotal(IReadOnlyList<double> values)
    {
        var result = new double[values.Count];
        var total = 0d;
        for (var index = 0; index < values.Count; index++) result[index] = total += values[index];
        return result;
    }

    private static double MetricValue(GraphRow row, DashboardGraphMeasure measure) => measure switch
    {
        DashboardGraphMeasure.ListeningTime => row.Listens * DurationSeconds(row.Track.Duration),
        DashboardGraphMeasure.TracksListened => 1,
        _ => row.Listens
    };

    private static string GroupName(TrackSnapshot track, DashboardGraphGrouping grouping) => grouping switch
    {
        DashboardGraphGrouping.Artist => EmptyAsUnknown(track.Artist, "Unknown artist"),
        DashboardGraphGrouping.Album => EmptyAsUnknown(track.Album, "Unknown album"),
        DashboardGraphGrouping.Track => EmptyAsUnknown(track.Title, "Unknown track"),
        _ => "All listening"
    };

    private static double DurationSeconds(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    private static string EmptyAsUnknown(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static DateTimeOffset ToEastern(DateTimeOffset utc) => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, "Eastern Standard Time");
    private static DashboardGraph Empty(string message) => new([], [], [], "No chart data", message);

    private sealed record GraphInterval(DateTimeOffset? Start, DateTimeOffset End, bool IsBaseline, IReadOnlyList<GraphRow> Rows);
    private sealed record GraphRow(TrackSnapshot Track, long Listens, long FirstSeenListens);
    private sealed record GraphEntry(DateTime Bucket, GraphInterval Interval, string Category, string TrackId, double Value);
    private sealed record GraphBucket(DateTime Key, string Label, string Tooltip, IReadOnlyList<GraphInterval> Intervals);
}
