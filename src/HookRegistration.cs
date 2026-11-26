using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace XunXun;

static class HookRegistration
{
    private static readonly (string Name, string? Matcher)[] Common =
        [("Stop", null), ("PermissionRequest", null), ("UserPromptSubmit", null), ("PostToolUse", null), ("SessionEnd", null)];
    private const string QuestionMatcher = "^AskUserQuestion$";
    private static readonly (string Name, string? Matcher)[] ClaudeQuestions =
        [("PreToolUse", QuestionMatcher), ("Elicitation", null), ("ElicitationResult", null)];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal sealed record Target(string Name, string Cli, string File);

    public static void Offer(Window owner)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string executable = Environment.ProcessPath is { } processPath
            && !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath : Path.Combine(AppContext.BaseDirectory, "XunXun.exe");
        if (!File.Exists(executable)) return;

        var targets = new[] {
            UserTarget("Claude Code", "claude", "CLAUDE_CONFIG_DIR", ".claude", "settings.json", profile),
            UserTarget("Codex CLI", "codex", "CODEX_HOME", ".codex", "hooks.json", profile)
        };
        var missing = new List<Target>();
        var unavailable = new List<string>();
        var installDirectories = InstallDirectories(profile).ToArray();
        foreach (var target in targets)
        {
            try
            {
                if (!Installed(target.Cli, installDirectories)) continue;
                if (!Registered(target, executable)) missing.Add(target);
            }
            catch (Exception error) when (ConfigError(error))
            {
                unavailable.Add($"{target.Name} 的 Hook 配置无法读取或格式不受支持。\n{target.File}");
            }
        }
        if (unavailable.Count > 0)
            HookSetupWindow.Report(owner, "Hook 配置检查", unavailable);
        if (missing.Count == 0) return;

        var approved = HookSetupWindow.Ask(owner, missing, profile);
        if (approved.Length == 0) return;

        var result = new List<string>();
        foreach (var target in approved)
        {
            try
            {
                Register(target, executable);
                result.Add(target.Cli == "codex"
                    ? "Codex CLI 已注册。请重新打开会话，并在 /hooks 中审查并信任巡巡的 Hook。"
                    : Read(target.File).Document["disableAllHooks"] is JsonValue flag
                        && flag.TryGetValue<bool>(out bool disabled) && disabled
                        ? "Claude Code 已注册。当前配置关闭了所有 Hook，开启后即可接收提醒。"
                        : "Claude Code 已注册。请重新打开会话以加载 Hook。");
            }
            catch (Exception error) when (ConfigError(error))
            {
                result.Add($"{target.Name} 注册未完成。请检查配置文件格式、访问权限或文件是否正在修改。\n{target.File}");
            }
        }
        HookSetupWindow.Report(owner, "Hook 注册结果", result);
    }

    internal static bool Registered(Target target, string executable) =>
        !Merge(Read(target.File).Document, target, executable);

    internal static bool Register(Target target, string executable)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (original, document) = Read(target.File);
            if (!Merge(document, target, executable)) return false;

            var directory = Path.GetDirectoryName(target.File)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $".xunxun-{Guid.NewGuid():N}.tmp");
            string backup = $"{target.File}.xunxun-{Guid.NewGuid():N}.bak";
            try
            {
                File.WriteAllText(temporary, document.ToJsonString(Indented) + Environment.NewLine);
                var latest = ReadBytes(target.File);
                if (original is null)
                {
                    if (latest is not null) continue;
                    try { File.Move(temporary, target.File); }
                    catch (IOException) when (File.Exists(target.File)) { continue; }
                }
                else
                {
                    if (latest is null || !original.AsSpan().SequenceEqual(latest)) continue;
                    File.Replace(temporary, target.File, backup);
                }
                return true;
            }
            finally { File.Delete(temporary); }
        }
        throw new IOException("The hook configuration changed during registration.");
    }

    private static bool Merge(JsonObject root, Target target, string executable)
    {
        bool codex = target.Cli == "codex";
        var hooks = root["hooks"] switch
        {
            null => (JsonObject)(root["hooks"] = new JsonObject()),
            JsonObject configured => configured,
            _ => throw new JsonException("Expected a hooks object.")
        };
        bool changed = false;
        var events = codex ? Common.Append(("Interrupt", null)) : Common.Concat(ClaudeQuestions);
        foreach (var (eventName, matcher) in events)
        {
            var groups = hooks[eventName] switch
            {
                null => (JsonArray)(hooks[eventName] = new JsonArray()),
                JsonArray configured => configured,
                _ => throw new JsonException("Expected an array of hook matchers.")
            };
            var wanted = Observer(codex, executable);
            var existing = groups.OfType<JsonObject>().Where(group => CoversMatcher(group, matcher))
                .SelectMany(group => (group["hooks"] as JsonArray)?.OfType<JsonObject>()
                    ?? Enumerable.Empty<JsonObject>()).ToArray();
            if (existing.Any(handler => IsObserver(handler, wanted))) continue;

            // Update a known older XunXun path in place after consent.
            var previous = existing.FirstOrDefault(handler => PreviousPath(handler, codex) is { } path
                && IsObserver(handler, Observer(codex, path)));
            if (previous is null)
            {
                var group = new JsonObject { ["hooks"] = new JsonArray(wanted) };
                if (matcher is not null) group["matcher"] = matcher;
                groups.Add(group);
            }
            else
            {
                previous["command"] = wanted["command"]!.DeepClone();
                if (codex)
                    previous[previous.ContainsKey("command_windows") ? "command_windows" : "commandWindows"]
                        = wanted["commandWindows"]!.DeepClone();
            }
            changed = true;
        }
        return changed;
    }

    private static JsonObject Observer(bool codex, string executable)
    {
        var handler = new JsonObject { ["type"] = "command", ["timeout"] = 3 };
        if (codex)
        {
            string unix = executable.Replace('\\', '/').Replace("'", "'\\''");
            string powershell = executable.Replace("'", "''");
            handler["command"] = $"'{unix}' --hook codex";
            handler["commandWindows"] = $"& '{powershell}' --hook codex";
        }
        else
        {
            handler["command"] = executable;
            handler["args"] = new JsonArray("--hook", "claude");
        }
        return handler;
    }

    private static bool IsObserver(JsonObject handler, JsonObject wanted) =>
        Value(handler["type"]) == "command"
        && string.Equals(Value(handler["command"]), Value(wanted["command"]), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Value(handler["commandWindows"] ?? handler["command_windows"]),
            Value(wanted["commandWindows"]), StringComparison.OrdinalIgnoreCase)
        && JsonNode.DeepEquals(handler["args"], wanted["args"])
        && handler["if"] is null;

    private static string? PreviousPath(JsonObject handler, bool codex)
    {
        var path = Value(handler["command"]);
        if (codex)
        {
            const string suffix = "' --hook codex";
            var command = Value(handler["commandWindows"] ?? handler["command_windows"]);
            path = command is not null && command.StartsWith("& '", StringComparison.Ordinal)
                && command.EndsWith(suffix, StringComparison.Ordinal)
                ? command[3..^suffix.Length].Replace("''", "'") : null;
        }
        return path is not null && Path.IsPathFullyQualified(path)
            && string.Equals(Path.GetFileName(path), "XunXun.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool CoversMatcher(JsonObject group, string? matcher) =>
        group["matcher"] is null || Value(group["matcher"]) is "" or "*" or ".*" or "^.*$"
        || (matcher == QuestionMatcher && Value(group["matcher"]) is QuestionMatcher or "AskUserQuestion");

    private static string? Value(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static (byte[]? Original, JsonObject Document) Read(string path)
    {
        var bytes = ReadBytes(path);
        if (bytes is null) return (null, new JsonObject());
        ReadOnlySpan<byte> content = bytes.AsSpan();
        if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
        var document = JsonNode.Parse(content) as JsonObject ?? throw new JsonException("Expected an object.");
        // Materialize the root eagerly so duplicate keys are rejected before any write.
        _ = document.Count;
        return (bytes, document);
    }

    private static byte[]? ReadBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static Target UserTarget(string name, string cli, string variable, string defaultDirectory, string file,
        string profile)
    {
        string? home = Environment.GetEnvironmentVariable(variable);
        return new Target(name, cli, Path.Combine(string.IsNullOrWhiteSpace(home)
            ? Path.Combine(profile, defaultDirectory) : home, file));
    }

    private static IEnumerable<string> InstallDirectories(string profile)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(folder)) yield return folder.Trim().Trim('"');
        yield return Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? profile, ".local", "bin");
        if (Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } roaming)
            yield return Path.Combine(roaming, "npm");
        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
    }

    internal static bool Installed(string tool, IEnumerable<string> directories) =>
        directories.Any(folder => new[] { ".exe", ".cmd", ".bat", ".ps1" }
            .Any(extension => File.Exists(Path.Combine(folder, tool + extension))));

    private static bool ConfigError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or ArgumentException;
}
