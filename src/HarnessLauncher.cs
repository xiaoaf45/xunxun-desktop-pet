using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XunXun;

static class HarnessLauncher
{
    internal sealed record Tool(string Name, string Executable, string ApprovalFlag, string ApprovalValue);

    // Read the terminal's own HWND. The foreground window can change while it starts.
    private const string Startup = """
        $ErrorActionPreference = 'Stop'
        $host.UI.RawUI.WindowTitle = $env:XUNXUN_LAUNCH_TITLE
        Add-Type -Namespace XunXun -Name ConsoleBinding -MemberDefinition @'
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        '@
        $window = [XunXun.ConsoleBinding]::GetConsoleWindow()
        [uint32]$owner = 0
        if (![XunXun.ConsoleBinding]::IsWindowVisible($window) -or
            ![XunXun.ConsoleBinding]::GetWindowThreadProcessId($window, [ref]$owner)) {
            throw 'XunXun could not bind this console window.'
        }
        $env:XUNXUN_HWND = $window.ToInt64()
        $env:XUNXUN_PID = $owner
        $env:XUNXUN_STARTED = (Get-Process -Id $owner).StartTime.ToUniversalTime().Ticks
        $tool = $env:XUNXUN_LAUNCH_TOOL
        $arguments = @()
        if ($env:XUNXUN_LAUNCH_FLAG) {
            $arguments = @($env:XUNXUN_LAUNCH_FLAG, $env:XUNXUN_LAUNCH_VALUE)
        }
        Remove-Item Env:XUNXUN_LAUNCH_TOOL, Env:XUNXUN_LAUNCH_TITLE,
            Env:XUNXUN_LAUNCH_FLAG, Env:XUNXUN_LAUNCH_VALUE -ErrorAction SilentlyContinue
        & $tool @arguments
        """;

    public static Tool[] AvailableTools()
    {
        var directories = HookRegistration.InstallDirectories(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).ToArray();
        var result = new List<Tool>();
        if (HookRegistration.FindExecutable("claude", directories) is { } claude)
            result.Add(new("Claude Code", claude, "--permission-mode", "bypassPermissions"));
        if (HookRegistration.FindExecutable("codex", directories) is { } codex)
            result.Add(new("Codex CLI", codex, "--ask-for-approval", "never"));
        return result.ToArray();
    }

    public static string? ExistingDirectory(string text)
    {
        string path = text.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static ProcessStartInfo Terminal(Tool tool, string directory, bool noApproval)
    {
        string system = Environment.SystemDirectory;
        var info = new ProcessStartInfo(Path.Combine(system, "conhost.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        foreach (var argument in new[] { Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoLogo", "-NoProfile", "-NoExit", "-Command", Startup })
            info.ArgumentList.Add(argument);
        info.Environment["XUNXUN_LAUNCH_TITLE"] = $"{tool.Name} · {Path.GetFileName(directory)}";
        info.Environment["XUNXUN_LAUNCH_TOOL"] = tool.Executable;
        info.Environment["XUNXUN_LAUNCH_FLAG"] = noApproval ? tool.ApprovalFlag : "";
        info.Environment["XUNXUN_LAUNCH_VALUE"] = noApproval ? tool.ApprovalValue : "";
        info.Environment.Remove("ELECTRON_RUN_AS_NODE");
        return info;
    }

    public static void Start(Tool tool, string directory, bool noApproval)
    {
        using var terminal = Process.Start(Terminal(tool, directory, noApproval));
    }
}

sealed class ProjectHistory(string file)
{
    private const int MaxEntries = 12;

    public ProjectHistory() : this(Path.Combine(HookRegistration.AppDataDirectory, "project-history.json")) { }

    public string[] Read()
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEntries).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Remember(string path)
    {
        var paths = Read().Prepend(path).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEntries);
        string directory = Path.GetDirectoryName(file)!;
        string temporary = Path.Combine(directory, $".project-history-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(paths));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
