using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XunXun;

static class HarnessLauncher
{
    internal sealed record Tool(string Name, string Executable, string ApprovalFlag, string ApprovalValue);

    // Read the launch window's HWND without assuming it is in the foreground.
    private const string Startup = """
        $ErrorActionPreference = 'Stop'
        Set-Location -LiteralPath $env:XUNXUN_LAUNCH_DIRECTORY
        $utf8 = [Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = $utf8
        [Console]::OutputEncoding = $utf8
        $OutputEncoding = $utf8
        Add-Type -Namespace XunXun -Name ConsoleBinding -MemberDefinition @'
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        '@
        $title = $env:XUNXUN_LAUNCH_TITLE
        $host.UI.RawUI.WindowTitle = $title
        if ($env:XUNXUN_LAUNCH_TERMINAL) {
            $deadline = [DateTime]::UtcNow.AddSeconds(3)
            do {
                $window = [XunXun.ConsoleBinding]::FindWindow('CASCADIA_HOSTING_WINDOW_CLASS', $title)
                if ([XunXun.ConsoleBinding]::IsWindowVisible($window)) { break }
                Start-Sleep -Milliseconds 80
            } while ([DateTime]::UtcNow -lt $deadline)
        } else {
            $window = [XunXun.ConsoleBinding]::GetConsoleWindow()
        }
        [uint32]$owner = 0
        if ([XunXun.ConsoleBinding]::IsWindowVisible($window) -and
            [XunXun.ConsoleBinding]::GetWindowThreadProcessId($window, [ref]$owner)) {
            $env:XUNXUN_HWND = $window.ToInt64()
            $env:XUNXUN_PID = $owner
            $env:XUNXUN_STARTED = (Get-Process -Id $owner).StartTime.ToUniversalTime().Ticks
        } else {
            Write-Warning 'XunXun could not bind this terminal window. You can still use the CLI here.'
        }
        $host.UI.RawUI.WindowTitle = $env:XUNXUN_LAUNCH_LABEL
        $tool = $env:XUNXUN_LAUNCH_TOOL
        $arguments = @()
        if ($env:XUNXUN_LAUNCH_FLAG) {
            $arguments = @($env:XUNXUN_LAUNCH_FLAG, $env:XUNXUN_LAUNCH_VALUE)
        }
        Remove-Item Env:XUNXUN_LAUNCH_* -ErrorAction SilentlyContinue
        & $tool @arguments
        exit $LASTEXITCODE
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

    internal static ProcessStartInfo Terminal(Tool tool, string directory, bool noApproval, string? terminal)
    {
        string system = Environment.SystemDirectory;
        string label = $"{tool.Name} · {Path.GetFileName(directory)}";
        string title = terminal is null ? label : $"XunXun-{Guid.NewGuid():N}";
        var info = new ProcessStartInfo(terminal ?? Path.Combine(system, "conhost.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        if (terminal is not null)
            foreach (string argument in new[] { "-w", "new", "new-tab", "--title", title,
                "--useApplicationTitle", "--inheritEnvironment" })
                info.ArgumentList.Add(argument);
        foreach (var argument in new[] { Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoLogo", "-NoProfile", "-Command", "& ([scriptblock]::Create($env:XUNXUN_LAUNCH_SCRIPT))" })
            info.ArgumentList.Add(argument);
        info.Environment["XUNXUN_LAUNCH_SCRIPT"] = Startup;
        info.Environment["XUNXUN_LAUNCH_DIRECTORY"] = directory;
        info.Environment["XUNXUN_LAUNCH_TERMINAL"] = terminal is null ? "" : "1";
        info.Environment["XUNXUN_LAUNCH_TITLE"] = title;
        info.Environment["XUNXUN_LAUNCH_LABEL"] = label;
        info.Environment["XUNXUN_LAUNCH_TOOL"] = tool.Executable;
        info.Environment["XUNXUN_LAUNCH_FLAG"] = noApproval ? tool.ApprovalFlag : "";
        info.Environment["XUNXUN_LAUNCH_VALUE"] = noApproval ? tool.ApprovalValue : "";
        info.Environment.Remove("ELECTRON_RUN_AS_NODE");
        // The desktop terminal should use the user's color preferences, even when
        // XunXun itself was started by a noninteractive coding runner.
        UseDesktopEnvironment(info, "NO_COLOR");
        if (info.Environment.TryGetValue("TERM", out var term)
            && string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            UseDesktopEnvironment(info, "TERM");
        foreach (string variable in new[] { "XUNXUN_HWND", "XUNXUN_PID", "XUNXUN_STARTED" })
            info.Environment.Remove(variable);
        return info;
    }

    private static void UseDesktopEnvironment(ProcessStartInfo info, string name)
    {
        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        if (value is null) info.Environment.Remove(name);
        else info.Environment[name] = value;
    }

    internal static string? WindowsTerminal()
    {
        string aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        string[] aliasesByPackage = [
            Path.Combine(aliases, "wt.exe"),
            Path.Combine(aliases, "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "wt.exe"),
            Path.Combine(aliases, "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "wt.exe")
        ];
        return aliasesByPackage.FirstOrDefault(File.Exists) ?? HookRegistration.FindExecutable("wt",
            HookRegistration.InstallDirectories(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    public static void Start(Tool tool, string directory, bool noApproval)
    {
        if (WindowsTerminal() is { } terminal)
        {
            try
            {
                using var process = Process.Start(Terminal(tool, directory, noApproval, terminal));
                return;
            }
            catch (Win32Exception) { }
        }
        using var fallback = Process.Start(Terminal(tool, directory, noApproval, terminal: null));
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
