using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XunXun;

static class T3Completion
{
    public const string Provider = "T3 Code";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record ThreadState(string ThreadId, string? Title, string? Link, string? Status,
        string? LatestRunId, DateTimeOffset CreatedAt, string? RelationshipToParent,
        JsonElement[]? PendingBackgroundTasks, bool BlockedOnThreadWait);

    internal sealed class Tracker
    {
        private readonly Dictionary<string, (string? Run, bool Done)> previous = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> notified = new(StringComparer.Ordinal);
        private DateTimeOffset? started;

        public IEnumerable<Notice> Observe(ThreadState[] snapshot, DateTimeOffset readStarted)
        {
            foreach (var thread in snapshot)
            {
                if (string.IsNullOrEmpty(thread.ThreadId) || thread.RelationshipToParent == "subagent") continue;
                bool done = thread.Status == "completed" && !thread.BlockedOnThreadWait
                    && thread.PendingBackgroundTasks is not { Length: > 0 };
                bool advance = previous.TryGetValue(thread.ThreadId, out var before)
                    ? !before.Done || before.Run != thread.LatestRunId
                    : started is { } baseline && thread.CreatedAt >= baseline;
                previous[thread.ThreadId] = (thread.LatestRunId, done);
                if (!done || !advance || thread.LatestRunId is not { Length: > 0 } run) continue;
                if (notified.TryGetValue(thread.ThreadId, out var last) && last == run) continue;
                notified[thread.ThreadId] = run;
                var title = string.IsNullOrWhiteSpace(thread.Title) ? "会话已完成" : thread.Title.Trim();
                yield return new Notice(Provider, thread.ThreadId, Mood.Done, false,
                    title.Length > 80 ? title[..80] : title, 0, 0, 0, thread.Link);
            }
            started ??= readStarted;
        }
    }

    public static async Task Watch(Action<Notice> receive, CancellationToken token)
    {
        string[] projects;
        try
        {
            string config = Path.Combine(AppContext.BaseDirectory, "t3-projects.json");
            if (!File.Exists(config)) return;
            projects = (JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(config, token)
                .ConfigureAwait(false)) ?? [])
                .Where(id => Guid.TryParseExact(id, "D", out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or OperationCanceledException) { return; }
        if (projects.Length == 0) return;

        var trackers = projects.Select(_ => new Tracker()).ToArray();
        try
        {
            while (!token.IsCancellationRequested)
            {
                for (int i = 0; i < projects.Length; i++)
                {
                    var readStarted = DateTimeOffset.UtcNow;
                    var rows = await ReadProject(projects[i], token).ConfigureAwait(false);
                    if (rows is null) continue;
                    foreach (var notice in trackers[i].Observe(rows, readStarted)) receive(notice);
                }
                await Task.Delay(TimeSpan.FromSeconds(7), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static async Task<ThreadState[]?> ReadProject(string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "D", out var project)) return null;
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /c t3 thread list --json --project {project:D}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return process.ExitCode == 0 ? JsonSerializer.Deserialize<ThreadState[]>(output.Result, Json) : null;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            token.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or JsonException) { return null; }
    }

    public static Uri? ThreadLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "app.t3.codes" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.AbsolutePath.StartsWith("/threads/", StringComparison.Ordinal) ? uri : null;
}
