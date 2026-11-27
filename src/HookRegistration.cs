using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
    internal const string ClaudeCommand = "powershell.exe -NoProfile -NonInteractive -Command '"
        + "$relay = Join-Path $env:LOCALAPPDATA XunXun/HookRelay/XunXun.exe; "
        + "if ([IO.File]::Exists($relay)) { & $relay --hook claude | Out-Null }; exit 0'";
    private const string ManualClaudePrefix = "$relay = '";
    private const string ManualClaudeSuffix = "'; if ([IO.File]::Exists($relay)) { & $relay --hook claude | Out-Null }; exit 0";
    private const string LegacyEncodedPrefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
    private const string LegacyScriptPrefix = "$observer = '";
    private const string LegacyScriptSuffix = "'\n"
        + "if (Test-Path -LiteralPath $observer -PathType Leaf) {\n"
        + "  $utf8 = [Text.UTF8Encoding]::new($false)\n"
        + "  [Console]::InputEncoding = $utf8\n"
        + "  $OutputEncoding = $utf8\n"
        + "  [Console]::In.ReadToEnd() | & $observer --hook claude | Out-Null\n"
        + "}\nexit 0\n";

    internal sealed record Target(string Name, string Cli, string File);

    public static void Offer(Window owner, bool manual = false)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string executable = Environment.ProcessPath is { } processPath
            && !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath : Path.Combine(AppContext.BaseDirectory, "XunXun.exe");
        if (!File.Exists(executable)) return;
        string relayExecutable = Path.Combine(AppDataDirectory, "HookRelay", "XunXun.exe");

        var targets = new[] {
            UserTarget("Claude Code", "claude", "CLAUDE_CONFIG_DIR", ".claude", "settings.json", profile),
            UserTarget("Codex CLI", "codex", "CODEX_HOME", ".codex", "hooks.json", profile)
        };
        var missing = new List<Target>();
        var unavailable = new List<string>();
        var skipped = ReadSkipped();
        var installDirectories = InstallDirectories(profile).ToArray();
        foreach (var target in targets)
        {
            try
            {
                if ((!manual && skipped.Contains(target.Cli)) || !Installed(target.Cli, installDirectories)) continue;
                if (!File.Exists(relayExecutable) || !Registered(target, relayExecutable)) missing.Add(target);
            }
            catch (Exception error) when (ConfigError(error))
            {
                unavailable.Add($"{target.Name} 的 Hook 配置无法读取或格式不受支持。\n{target.File}");
            }
        }
        if (unavailable.Count > 0)
            HookSetupWindow.Report(owner, "Hook 配置检查", unavailable);
        if (missing.Count == 0)
        {
            if (manual && unavailable.Count == 0)
                HookSetupWindow.Report(owner, "任务提醒", ["没有需要注册的已安装工具。"]);
            return;
        }

        var approved = HookSetupWindow.Ask(owner, missing, profile);
        foreach (var target in missing)
        {
            if (approved.Contains(target)) skipped.Remove(target.Cli);
            else skipped.Add(target.Cli);
        }
        WriteSkipped(skipped);
        if (approved.Length == 0) return;

        try { InstallRelay(executable, relayExecutable); }
        catch (Exception error) when (ConfigError(error))
        {
            HookSetupWindow.Report(owner, "Hook 注册结果", [$"无法保存巡巡的 Hook 接收程序。请检查此目录的访问权限。\n{relayExecutable}"]);
            return;
        }

        var result = new List<string>();
        foreach (var target in approved)
        {
            try
            {
                Register(target, relayExecutable);
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

    internal static void InstallRelay(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        if (ReadBytes(destination) is { } installed
            && installed.AsSpan().SequenceEqual(File.ReadAllBytes(source))) return;

        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".relay-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static bool Register(Target target, string executable)
    {
        string path = Path.GetFullPath(target.File);
        string lockId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var gate = new Mutex(false, @"Global\XunXun-Hook-" + lockId);
        bool locked;
        try { locked = gate.WaitOne(TimeSpan.FromSeconds(4)); }
        catch (AbandonedMutexException) { locked = true; }
        if (!locked) throw new IOException("Another XunXun instance is registering hooks.");
        try { return WriteRegistration(target with { File = path }, executable); }
        finally { gate.ReleaseMutex(); }
    }

    private static bool WriteRegistration(Target target, string executable)
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
            var owned = (from matchGroup in groups.OfType<JsonObject>()
                         where matchGroup["hooks"] is JsonArray
                         let handlers = (JsonArray)matchGroup["hooks"]!
                         from handler in handlers.OfType<JsonObject>()
                         let path = OwnedPath(handler, codex, executable)
                         where path is not null
                         select (Group: matchGroup, Handlers: handlers, Handler: handler, Path: path)).ToArray();
            var working = owned.FirstOrDefault(entry => CoversMatcher(entry.Group, matcher)
                && IsObserver(entry.Handler, Observer(codex, entry.Path!))
                && string.Equals(entry.Path, executable, StringComparison.OrdinalIgnoreCase));
            JsonObject? keep = working.Handler;
            if (keep is null)
            {
                keep = owned.FirstOrDefault(entry => CoversMatcher(entry.Group, matcher)).Handler;
                if (keep is null)
                {
                    var group = new JsonObject { ["hooks"] = new JsonArray(wanted) };
                    if (matcher is not null) group["matcher"] = matcher;
                    groups.Add(group);
                    keep = wanted;
                }
                else
                {
                    keep["command"] = wanted["command"]!.DeepClone();
                    if (codex)
                        keep[keep.ContainsKey("command_windows") ? "command_windows" : "commandWindows"]
                            = wanted["commandWindows"]!.DeepClone();
                    else
                    {
                        keep.Remove("args");
                        keep.Remove("shell");
                        keep["timeout"] = wanted["timeout"]!.DeepClone();
                    }
                }
                changed = true;
            }
            foreach (var entry in owned)
            {
                if (ReferenceEquals(entry.Handler, keep)) continue;
                entry.Handlers.Remove(entry.Handler);
                if (entry.Handlers.Count == 0) groups.Remove(entry.Group);
                changed = true;
            }
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
            handler["command"] = ClaudeCommand;
            handler["timeout"] = 5;
        }
        return handler;
    }

    private static bool IsObserver(JsonObject handler, JsonObject wanted) =>
        Value(handler["type"]) == "command"
        && string.Equals(Value(handler["command"]), Value(wanted["command"]),
            wanted.ContainsKey("commandWindows") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(Value(handler["commandWindows"] ?? handler["command_windows"]),
            Value(wanted["commandWindows"]), StringComparison.OrdinalIgnoreCase)
        && JsonNode.DeepEquals(handler["args"], wanted["args"])
        && handler["if"] is null;

    internal static string ManualClaudeCommand(string executable) =>
        ManualClaudePrefix + executable.Replace("'", "''") + ManualClaudeSuffix;

    private static string? OwnedPath(JsonObject handler, bool codex, string current)
    {
        if (Value(handler["type"]) != "command" || handler["if"] is not null) return null;
        var path = Value(handler["command"]);
        if (!codex && IsObserver(handler, Observer(false, current))) return current;
        bool previousShellCommand = false;
        if (codex)
        {
            const string suffix = "' --hook codex";
            var command = Value(handler["commandWindows"] ?? handler["command_windows"]);
            path = command is not null && command.StartsWith("& '", StringComparison.Ordinal)
                && command.EndsWith(suffix, StringComparison.Ordinal)
                ? command[3..^suffix.Length].Replace("''", "'") : null;
        }
        else if (path is not null && path.StartsWith(LegacyEncodedPrefix, StringComparison.Ordinal))
        {
            try
            {
                string script = Encoding.Unicode.GetString(Convert.FromBase64String(path[LegacyEncodedPrefix.Length..]));
                path = ScriptPath(script, LegacyScriptPrefix, LegacyScriptSuffix);
                if (path is not null && script != LegacyScriptPrefix + path.Replace("'", "''") + LegacyScriptSuffix)
                    return null;
                previousShellCommand = true;
            }
            catch (FormatException) { return null; }
        }
        else if (path is not null && Value(handler["shell"]) == "powershell")
        {
            var manualPath = ScriptPath(path, ManualClaudePrefix, ManualClaudeSuffix);
            if (manualPath is not null && path == ManualClaudeCommand(manualPath))
            {
                path = manualPath;
                previousShellCommand = true;
            }
        }
        if (path is null || !Path.IsPathFullyQualified(path)
            || (!string.Equals(path, current, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(path), "XunXun.exe", StringComparison.OrdinalIgnoreCase))) return null;
        if (IsObserver(handler, Observer(codex, path))) return path;
        if (previousShellCommand && handler["args"] is null) return path;
        if (!codex && string.Equals(Value(handler["command"]), path, StringComparison.OrdinalIgnoreCase)
            && (JsonNode.DeepEquals(handler["args"], new JsonArray("--hook", "claude"))
                || handler["args"] is null && handler["timeout"] is JsonValue time
                    && time.TryGetValue<int>(out var seconds) && seconds == 3)) return path;
        return null;
    }

    private static string? ScriptPath(string script, string prefix, string suffix) =>
        script.StartsWith(prefix, StringComparison.Ordinal) && script.EndsWith(suffix, StringComparison.Ordinal)
        && script.Length > prefix.Length + suffix.Length
            ? script[prefix.Length..^suffix.Length].Replace("''", "'") : null;

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

    internal static IEnumerable<string> InstallDirectories(string profile)
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
        FindExecutable(tool, directories) is not null;

    internal static string? FindExecutable(string tool, IEnumerable<string> directories)
    {
        foreach (var folder in directories.Where(Path.IsPathFullyQualified))
            foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            {
                var path = Path.Combine(folder, tool + extension);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    private static bool ConfigError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or ArgumentException;

    internal static string AppDataDirectory => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XunXun");
    private static string PreferenceFile => Path.Combine(AppDataDirectory, "skipped-hooks.json");

    private static HashSet<string> ReadSkipped()
    {
        try
        {
            return new HashSet<string>(JsonSerializer.Deserialize<string[]>(ReadBytes(PreferenceFile) ?? "[]"u8.ToArray())
                ?? [], StringComparer.Ordinal);
        }
        catch (Exception error) when (ConfigError(error)) { return new HashSet<string>(StringComparer.Ordinal); }
    }

    private static void WriteSkipped(HashSet<string> skipped)
    {
        if (skipped.SetEquals(ReadSkipped())) return;
        try
        {
            string file = PreferenceFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(skipped));
        }
        catch (Exception error) when (ConfigError(error)) { }
    }
}
