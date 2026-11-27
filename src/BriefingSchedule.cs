using System;
using System.Collections.Generic;
using System.Linq;

namespace XunXun;

sealed record BriefingFailure(BriefingSource Source, string Key, string Message);

sealed record BriefingState(DateOnly? FetchDay = null, DateOnly? ShownDay = null, DateOnly? RecapDay = null,
    BriefingSnapshot[]? Cache = null, BriefingFailure[]? Failures = null, string[]? Seen = null)
{
    public BriefingSnapshot? Find(BriefingOptions options, BriefingSource source) =>
        (Cache ?? []).FirstOrDefault(row => row?.Source == source && row.Key == options.Key(source)
            && row.Items is not null);

    public BriefingFailure? Failure(BriefingOptions options, BriefingSource source) =>
        (Failures ?? []).FirstOrDefault(row => row?.Source == source && row.Key == options.Key(source));

    public BriefingState WithResults(IEnumerable<BriefingResult> results)
    {
        var cache = (Cache ?? []).Where(row => row is not null && row.Items is not null)
            .DistinctBy(row => row.Source).ToDictionary(row => row.Source);
        var failures = new List<BriefingFailure>();
        foreach (var result in results)
        {
            if (result.Snapshot is { } snapshot) cache[result.Source] = snapshot;
            else failures.Add(new(result.Source, result.Key, result.Error ?? "来源暂不可用"));
        }
        return this with { Cache = cache.Values.ToArray(), Failures = failures.ToArray() };
    }
}

readonly record struct BriefingRow(BriefingSource Source, BriefingItem Item);

sealed record BriefingNotice(string Copy, bool Rain, string[] Seen);

static class BriefingSchedule
{
    private static readonly char[] KeywordSeparators = [',', '，', ';', '；', '、'];

    public static DateOnly Day(DateTimeOffset now) => DateOnly.FromDateTime(now.LocalDateTime);

    public static bool IsDue(bool enabled, string time, DateOnly? shownDay, DateTimeOffset now) =>
        enabled && shownDay != Day(now) && BriefingOptions.TryTime(time, out var scheduled)
        && TimeOnly.FromDateTime(now.LocalDateTime) >= scheduled;

    public static BriefingRow[] News(IEnumerable<BriefingSnapshot> sources, string keywords, DateTimeOffset now)
    {
        var words = keywords.Split(KeywordSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var rows = sources.Where(source => BriefingClient.IsNews(source.Source))
            .SelectMany(source => Rows(source))
            .Where(row => row.Item.Published is { } date && date >= now.AddDays(-7) && date <= now.AddMinutes(10));
        if (words.Length > 0)
            rows = rows.Where(row => words.Any(word =>
                row.Item.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
                || row.Item.Detail.Contains(word, StringComparison.OrdinalIgnoreCase)));
        return rows.OrderByDescending(row => row.Item.Published)
            .DistinctBy(row => row.Item.Id).Take(3).ToArray();
    }

    public static IEnumerable<BriefingRow> Rows(BriefingSnapshot source) =>
        source.Items.Where(item => item is { Id.Length: > 0, Title.Length: > 0, Detail: not null }
            && BriefingClient.CanOpen(source.Source, item.Url))
            .Select(item => new BriefingRow(source.Source, item));

    public static BriefingNotice Build(BriefingOptions options, BriefingState state, DateTimeOffset now)
    {
        var known = (state.Seen ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        var seen = new HashSet<string>(known, StringComparer.Ordinal);
        var snapshots = options.Sources().Where(source => state.Failure(options, source) is null)
            .Select(source => state.Find(options, source))
            .OfType<BriefingSnapshot>().Where(source => Day(source.FetchedAt) == Day(now)).ToArray();
        var items = snapshots.SelectMany(Rows).ToArray();
        var weather = items.FirstOrDefault(row => row.Source == BriefingSource.Weather).Item;
        var releases = items.Where(row => row.Source is BriefingSource.Claude or BriefingSource.Codex).ToArray();
        int updates = releases.Count(row => !seen.Contains(row.Item.Id) && known.Any(id =>
            id.StartsWith($"release/{row.Source}/", StringComparison.Ordinal)));
        var repos = items.Where(row => row.Source == BriefingSource.Github && !seen.Contains(row.Item.Id)).ToArray();
        var news = News(snapshots, options.Keywords, now).Where(row => !seen.Contains(row.Item.Id)).ToArray();
        var summary = new List<string>();
        if (weather is not null)
            summary.Add(weather.Rain ? $"{weather.Title}上午降水概率偏高，出门记得带伞" : $"{weather.Title}的预报已更新");
        if (updates > 0) summary.Add($"{updates} 个工具有新正式版");
        if (repos.Length > 0) summary.Add($"{repos.Length} 个新项目");
        if (news.Length > 0) summary.Add($"{news.Length} 条新资讯");
        string[] latest = releases.Concat(repos).Concat(news).Select(row => row.Item.Id).Concat(known)
            .Distinct(StringComparer.Ordinal).Take(300).ToArray();
        return new(string.Join("  ·  ", summary), weather?.Rain == true, latest);
    }
}

readonly record struct TaskRecap(int Done, int Approvals, int Questions)
{
    public bool HasWork => Done + Approvals + Questions != 0;

    public string Copy => $"今天 {Done} 条完成记录  ·  {Approvals} 条待审批  ·  {Questions} 条待回答";

    public static TaskRecap From(IEnumerable<TaskInbox.Entry> entries, DateTimeOffset now)
    {
        var rows = entries.ToArray();
        return new(rows.Count(row => row.Item.Mood == Mood.Done && BriefingSchedule.Day(row.ReceivedAt) == BriefingSchedule.Day(now)),
            rows.Count(row => row.Item.Mood == Mood.Approval), rows.Count(row => row.Item.Mood == Mood.Question));
    }
}
