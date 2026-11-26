using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XunXun;

enum Mood { Roam, Working, Approval, Question, Done, Interrupted, Ended }

sealed record Notice(string Provider, string Session, Mood Mood, bool Demo,
    string Project, long Window, uint Pid, long Started, string? Link = null)
{
    public static Notice Sample(string provider, Mood mood) =>
        new(provider, "sample", mood, true,
            provider == "Claude Code" ? "组件文档" : "测试工作区", 0, 0, 0);
}

static class Relay
{
    public static string Name { get; } =
        $"XunXun-v1-{WindowsIdentity.GetCurrent().User!.Value}-{Process.GetCurrentProcess().SessionId}";

    public static async Task Listen(Action<Notice> receive, string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(700);
                var bytes = new byte[4096];
                int count = await pipe.ReadAsync(bytes, timeout.Token).ConfigureAwait(false);
                if (count == 0 || !pipe.IsMessageComplete) continue;

                var item = JsonSerializer.Deserialize<Notice>(bytes.AsSpan(0, count));
                if (item is { Demo: false, Provider: "Claude Code" or "Codex CLI" }
                    && item.Session is { Length: > 0 and <= 120 }
                    && item.Project is { Length: <= 80 }
                    && Enum.IsDefined(item.Mood) && item.Mood != Mood.Roam)
                    receive(item);
            }
            catch (OperationCanceledException) { }
            catch (JsonException) { }
            catch (IOException) { if (!token.IsCancellationRequested) await Task.Delay(100).ConfigureAwait(false); }
        }
    }

    public static void FromHook(string provider, Stream input, string? pipeName = null)
    {
        try
        {
            using var body = JsonDocument.Parse(input);
            var json = body.RootElement;
            var session = Field(json, "session_id");
            if (session is null || session.Length > 120) return;
            var eventName = Field(json, "hook_event_name");
            var tool = Field(json, "tool_name");
            var mood = eventName switch
            {
                "UserPromptSubmit" or "PostToolUse" or "ElicitationResult" => Mood.Working,
                "PermissionRequest" when tool == "AskUserQuestion" => Mood.Question,
                "PermissionRequest" => Mood.Approval,
                "PreToolUse" when tool == "AskUserQuestion" => Mood.Question,
                "Elicitation" => Mood.Question,
                "Stop" => Mood.Done,
                "Interrupt" => Mood.Interrupted,
                "SessionEnd" => Mood.Ended,
                _ => Mood.Roam
            };
            if (mood == Mood.Roam) return;

            var project = Path.GetFileName(Path.TrimEndingDirectorySeparator(Field(json, "cwd") ?? ""));
            if (project.Length > 80) project = project[..80];
            long.TryParse(Environment.GetEnvironmentVariable("XUNXUN_HWND"), out long window);
            uint.TryParse(Environment.GetEnvironmentVariable("XUNXUN_PID"), out uint pid);
            long.TryParse(Environment.GetEnvironmentVariable("XUNXUN_STARTED"), out long started);
            var item = new Notice(provider, session, mood, false, project, window, pid, started);

            using var pipe = new NamedPipeClientStream(".", pipeName ?? Name, PipeDirection.Out);
            pipe.Connect(300);
            pipe.Write(JsonSerializer.SerializeToUtf8Bytes(item));
        }
        // Observing the harness must never affect its normal approval or tool behavior.
        catch (Exception error) when (error is JsonException or IOException or TimeoutException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
    }

    private static string? Field(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

static class WindowsNative
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    public static bool BringForward(Notice item)
    {
        var handle = new IntPtr(item.Window);
        if (handle == IntPtr.Zero || !IsWindowVisible(handle)
            || GetAncestor(handle, 2) != handle
            || GetWindowThreadProcessId(handle, out uint pid) == 0 || pid != item.Pid)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.StartTime.ToUniversalTime().Ticks != item.Started) return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { return false; }
        if (IsIconic(handle)) ShowWindowAsync(handle, 9);
        return SetForegroundWindow(handle);
    }
}
