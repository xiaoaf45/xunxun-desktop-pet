using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XunXun;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--hook", "claude" or "codex"])
        {
            Relay.FromHook(args[1] == "claude" ? "Claude Code" : "Codex CLI", Console.OpenStandardInput());
            return 0;
        }

        if (args is ["--self-test", var directory])
            return SelfTest(directory);

        using var single = new Mutex(true, @"Local\" + Relay.Name, out bool first);
        if (!first) return 0;
        using var shutdown = new CancellationTokenSource();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new PetWindow();
        _ = Relay.Listen(item => window.Dispatcher.InvokeAsync(() => window.Receive(item)), Relay.Name, shutdown.Token);
        _ = T3Completion.Watch(item => window.Dispatcher.InvokeAsync(() => window.Receive(item)), shutdown.Token);
        app.Exit += (_, _) => shutdown.Cancel();
        window.Show();
        if (!Array.Exists(args, arg => arg == "--live")) window.StartDemo();
        return app.Run();
    }

    private static int SelfTest(string directory)
    {
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new PetWindow(preview: true);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await CheckRelay();
                CheckRoaming();
                CheckDisplayCrossing();
                bool nativeCrossing = await CheckNativeCrossing(window);
                CheckT3Completion();
                CheckHighFivePointer();
                var iconBounds = await Task.Run(DesktopIcons.Read).WaitAsync(TimeSpan.FromSeconds(6));
                Require(!ReferenceEquals(PetCursors.Open, System.Windows.Input.Cursors.Hand)
                    && !ReferenceEquals(PetCursors.Pressed, System.Windows.Input.Cursors.Hand)
                    && !ReferenceEquals(PetCursors.Open, PetCursors.Pressed), "Custom cursors did not load");
                SavePreview(window, Path.Combine(directory, "xunxun-preview.png"));
                window.Receive(Notice.Sample("Codex CLI", Mood.Interrupted));
                SavePlayPreview(window, Path.Combine(directory, "xunxun-play.png"));
                var t3Notice = new Notice(T3Completion.Provider, "test-thread", Mood.Done, false,
                    "T3 completion test", 0, 0, 0, "https://app.t3.codes/threads/test-env/test-thread");
                window.Receive(t3Notice);
                Require(window.Current == t3Notice && window.OpenButton.IsEnabled, "T3 completion card could not open its thread");
                window.Receive(t3Notice with { Mood = Mood.Interrupted });
                var demo = Notice.Sample("Claude Code", Mood.Approval);
                window.Receive(demo);
                window.StartPlay(Play.Nap);
                window.StartPlay(Play.Nap);
                Require(window.CurrentPlay == Play.Wake, "Sleep control did not wake the pet");
                Require(window.Current == demo && window.OpenButton.IsEnabled, "Approval action missing");
                window.OpenButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var handle = window.DemoHandle("Claude Code");
                Require(handle != IntPtr.Zero && WindowsNative.IsWindowVisible(handle), "Demo target did not open");
                var process = Process.GetCurrentProcess();
                var live = demo with { Demo = false, Window = handle.ToInt64(), Pid = (uint)process.Id,
                    Started = process.StartTime.ToUniversalTime().Ticks };
                Require(!WindowsNative.BringForward(live with { Pid = live.Pid + 1 }), "Mismatched window owner accepted");
                await Task.Delay(250);
                bool activated = WindowsNative.GetForegroundWindow() == handle || WindowsNative.BringForward(live);
                if (activated)
                {
                    await Task.Delay(100);
                    Require(WindowsNative.GetForegroundWindow() == handle, "Wrong foreground window");
                }
                File.WriteAllText(Path.Combine(directory, "self-test.txt"),
                    "PASS: relay mappings, T3 completion filtering, notice and play visuals, cursor assets, high-five pointer, roaming, icon detour and sizing, cross-display pursuit and roaming, reminder during play, stale target guard. "
                    + (nativeCrossing ? "PASS: two physical displays crossed using replayed pointer positions. " : "")
                    + $"Desktop icon bounds read: {iconBounds.Length}. "
                    + (activated ? "PASS: native foreground." : "UNVERIFIED: Windows did not grant foreground to automated run."));
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(directory, "self-test.txt"), error.ToString());
                app.Shutdown(1);
            }
        };
        window.Show();
        return app.Run();
    }

    private static void CheckHighFivePointer()
    {
        var pet = new PetArt();
        var rest = pet.HighFiveContact(0);
        var lift = pet.HighFiveContact(Play.HighFive.Duration() / 2);
        Require(rest.Y - lift.Y > 30, "The high-five paw did not lift");
        pet.FacingLeft = true;
        Require(Math.Abs(pet.HighFiveContact(Play.HighFive.Duration() / 2).X + lift.X - 136) < 0.01,
            "The pointer contact did not mirror with the paw");

        var press = new Point(90, 62);
        var gesture = new HighFivePointer(press, rest);
        Require(gesture.TryFollow(press, rest, 0, out var initial) && initial == press,
            "The pointer jumped at the start of the high five");
        Require(gesture.TryFollow(initial, lift, 0.575, out var moved) && (moved - lift).Length < 1,
            "The pointer did not follow the paw");
        Require(!gesture.TryFollow(moved + new Vector(10, 0), rest, 0.7, out _)
            && !gesture.TryFollow(moved, rest, Play.HighFive.Duration(), out _),
            "The animation kept moving the pointer after release");
    }

    private static void CheckRoaming()
    {
        var bounds = new Rect(0, 0, 620, 480);
        var walker = new DesktopRoam();
        var dropped = new Point(164, 155);
        var target = new Point(340, 155);
        var cluster = new[] { new Rect(80, 84, 76, 56), new Rect(156, 84, 76, 73),
            new Rect(80, 168, 76, 56), new Rect(156, 168, 76, 73) };
        walker.Reset(0);
        Require(!walker.TrackPointer(dropped, target, bounds.Contains(target), 0.4), "Drag release kicked off a chase");
        var roaming = dropped;
        for (int frame = 0; frame < 90; frame++)
        {
            var next = walker.Step(roaming, bounds, frame / 30d, 1 / 30d, null, cluster);
            Require(bounds.Contains(next) && (next - roaming).Length <= 2.3, "Roaming sprinted after a drop");
            roaming = next;
        }
        Require((roaming - dropped).Length > 1, "Pet remained at the drop");
        Require(!walker.TrackPointer(dropped, new Point(540, 155), true, 1.0)
            && walker.TrackPointer(dropped, target, true, 1.15), "Nearby mouse movement failed to start pursuit");
        var chasing = dropped;
        for (int frame = 0; frame < 36; frame++)
            chasing = walker.Step(chasing, bounds, 1.15 + frame / 30d, 1 / 30d, target, cluster);
        Require(Math.Abs(chasing.X - 295) < 0.01 && chasing.Y == dropped.Y,
            "Pursuit failed to cross the area and stop short of the cursor");
        Require(!walker.TrackPointer(chasing, target, true, 3.5), "Pursuit did not expire after the mouse stopped");
        Require(DesktopRoam.ScaleFor(dropped, dropped, cluster) == DesktopRoam.CompactScale
            && DesktopRoam.ScaleFor(new Point(350, 155), new Point(352, 155), cluster) == 1,
            "Pet did not shrink near the icon cluster and restore its size after it");
        var beforeIcon = new Point(102, 150);
        var detour = DesktopRoam.WanderToward(beforeIcon, new Point(350, 150), bounds, 2,
            [new Rect(188, 128, 60, 44)]);
        Require(detour.X > beforeIcon.X && Math.Abs(detour.Y - beforeIcon.Y) > 0.8,
            "Pet did not detour around the isolated icon");
    }

    private static void CheckDisplayCrossing()
    {
        var left = new Rect(-1920, 0, 1920, 1080);
        var right = new Rect(0, 0, 1920, 1080);
        var leftWalk = new Rect(-1855, 71, 1790, 877);
        var rightWalk = new Rect(65, 71, 1790, 877);
        var leftDisplay = new DesktopRoam.Display("left", left, leftWalk);
        var rightDisplay = new DesktopRoam.Display("right", right, rightWalk);
        DesktopRoam.Display[] displays = [leftDisplay, rightDisplay];
        foreach (int direction in new[] { 1, -1 })
        {
            var walker = new DesktopRoam();
            var position = new Point(-100 * direction, 400);
            var pointer = new Point(110 * direction, 400);
            Require(!walker.TrackPointer(position, new Point(-185 * direction, 400), true, 0)
                && walker.TrackPointer(position, pointer, true, 0.1), "Pointer lost across the seam");
            for (int tick = 0; tick < 55; tick++)
            {
                bool onLeft = position.X < 0;
                var crossing = onLeft != (pointer.X < 0)
                    ? DesktopRoam.FindCrossing(position, onLeft ? left : right, onLeft ? right : left,
                        onLeft ? leftWalk : rightWalk, onLeft ? rightWalk : leftWalk)
                    : null;
                var next = walker.Step(position, onLeft ? leftWalk : rightWalk,
                    tick / 30d, 1 / 30d, pointer, [], crossing);
                Require((next.X - position.X) * direction >= -0.001 && (next - position).Length <= 5.01,
                    "Pet jumped or walked backwards during a screen crossing");
                position = next;
            }
            Require(position.X * direction >= 64.9, "Pet stopped at the display seam");

            walker = new DesktopRoam(new Random(1));
            position = new Point(-100 * direction, 400);
            for (int tick = 0; tick < 160; tick++)
            {
                var active = position.X < 0 ? leftDisplay : rightDisplay;
                var next = walker.Step(position, active.Walk, tick / 30d, 1 / 30d, null, [],
                    currentDisplay: active, displays: displays);
                Require((next - position).Length <= 2.3, "Cross-display roaming teleported or sprinted");
                position = next;
            }
            Require(position.X * direction >= 65, "Roaming never reached the adjacent screen without a pointer");
        }

        var offset = new Rect(0, 380, 1920, 1080);
        var offsetWalk = new Rect(65, 451, 1790, 877);
        var start = new Point(-100, 320);
        var route = DesktopRoam.FindCrossing(start, left, offset, leftWalk, offsetWalk);
        Require(route is { } path && path.Aim(start).Y == 451 && path.Aim(path.Entrance) == path.Exit,
            "Offset displays did not route through the overlapping edge");
        Require(DesktopRoam.FindCrossing(start, left, new Rect(50, 0, 1920, 1080), leftWalk, rightWalk) is null,
            "Pet routed across a gap with no monitor");
    }

    private static void CheckT3Completion()
    {
        var clock = DateTimeOffset.Parse("2026-11-26T00:00:00Z");
        const string link = "https://app.t3.codes/threads/test-env/test-thread";
        var thread = new T3Completion.ThreadState("thread", "Test", link, "completed", "run1",
            clock.AddMinutes(-1), null, [], false);
        var tracker = new T3Completion.Tracker();
        Require(!tracker.Observe([thread], clock).Any(), "Historical T3 task emitted a notice");
        var running = thread with { Status = "running", LatestRunId = "run2" };
        Require(!tracker.Observe([running], clock.AddSeconds(1)).Any(), "Running T3 task emitted a notice");
        var pending = new JsonElement[1];
        Require(!tracker.Observe(
            [running with { Status = "completed", PendingBackgroundTasks = pending }], clock.AddSeconds(2)).Any(),
            "T3 task emitted before its background work finished");
        var finished = running with { Status = "completed" };
        var received = tracker.Observe([finished], clock.AddSeconds(3)).Single();
        Require(received is { Provider: T3Completion.Provider, Mood: Mood.Done, Link: link }
            && !tracker.Observe([finished], clock.AddSeconds(4)).Any()
            && T3Completion.ThreadLink(link) is not null
            && T3Completion.ThreadLink("https://example.com/threads/test-env/test-thread") is null,
            "T3 completion did not emit once with the verified thread link");
    }

    private static async Task<bool> CheckNativeCrossing(PetWindow window)
    {
        var pair = System.Windows.Forms.Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).ToArray();
        if (pair.Length != 2 || pair[0].Bounds.Right != pair[1].Bounds.Left) return false;
        int top = Math.Max(pair[0].WorkingArea.Top, pair[1].WorkingArea.Top);
        int bottom = Math.Min(pair[0].WorkingArea.Bottom, pair[1].WorkingArea.Bottom);
        if (bottom - top < 320) return false;

        var previous = window.ScreenAnchor;
        int seam = pair[0].Bounds.Right;
        var mouse = System.Windows.Forms.Cursor.Position;
        int y = Math.Abs(mouse.Y - (top + 144)) > 150 ? top + 144 : bottom - 144;
        try
        {
            foreach (int direction in new[] { 1, -1 })
            {
                double time = direction == 1 ? 1 : 5;
                window.PlaceAnchor(new Point(seam - 125 * direction, y), time);
                await System.Windows.Threading.Dispatcher.Yield();
                window.AdvanceMotion(new System.Drawing.Point(seam - 310 * direction, y), time + 0.9, 0);
                var target = new System.Drawing.Point(seam + 70 * direction, y);
                for (int tick = 0; tick < 53; tick++)
                {
                    window.AdvanceMotion(target, time + 1.02 + tick / 30d, 1 / 30d);
                    await System.Windows.Threading.Dispatcher.Yield();
                }
                Require((window.ScreenAnchor.X - seam) * direction >= 20,
                    "The native window did not follow the replayed pointer across the physical display seam");
            }
            return true;
        }
        finally { window.PlaceAnchor(previous, 10); }
    }

    private static async Task CheckRelay()
    {
        using var stop = new CancellationTokenSource();
        string pipeName = Relay.Name + "-check-" + Guid.NewGuid().ToString("N");
        var received = System.Threading.Channels.Channel.CreateUnbounded<Notice>();
        var listener = Relay.Listen(item => received.Writer.TryWrite(item), pipeName, stop.Token);
        try
        {
            foreach (var (provider, hook, tool, mood) in new[]
            {
                ("Claude Code", "UserPromptSubmit", "", Mood.Working),
                ("Claude Code", "PermissionRequest", "Bash", Mood.Approval),
                ("Claude Code", "PreToolUse", "AskUserQuestion", Mood.Question),
                ("Claude Code", "PostToolUse", "AskUserQuestion", Mood.Working),
                ("Claude Code", "Elicitation", "", Mood.Question),
                ("Codex CLI", "PermissionRequest", "apply_patch", Mood.Approval),
                ("Codex CLI", "Stop", "", Mood.Done),
                ("Codex CLI", "Interrupt", "", Mood.Interrupted),
            })
            {
                using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("malformed"));
                Relay.FromHook(provider, invalid, pipeName);
                string body = System.Text.Json.JsonSerializer.Serialize(new
                    { session_id = "fixture", hook_event_name = hook, tool_name = tool, cwd = @"C:\projects\example" });
                await Task.Run(() =>
                {
                    using var payload = new MemoryStream(Encoding.UTF8.GetBytes(body));
                    Relay.FromHook(provider, payload, pipeName);
                });
                using var timeout = new CancellationTokenSource(2500);
                var item = await received.Reader.ReadAsync(timeout.Token);
                Require(item is { Session: "fixture", Project: "example", Demo: false }
                    && item.Mood == mood && item.Provider == provider, $"Incorrect relay mapping for {hook}");
            }
        }
        finally
        {
            stop.Cancel();
            await listener;
        }
    }

    private static void SavePreview(PetWindow window, string path)
    {
        const int width = 1172;
        const int height = 396;
        var composed = new DrawingVisual();
        using (var dc = composed.RenderOpen())
        {
            dc.DrawRectangle(PetWindow.ColorBrush("#191A2C"), null, new Rect(0, 0, width, height));
            PreviewText(dc, "巡巡", 27, "#FFF4D9", 28, 19);
            PreviewText(dc, "AI 桌宠  /  Windows 原型", 14, "#C3BFDA", 99, 30);
            var moods = new[] { Mood.Approval, Mood.Question, Mood.Done };
            var names = new[] { "审批提醒", "提问提醒", "完成提醒" };
            for (int i = 0; i < moods.Length; i++)
            {
                double x = 20 + i * 382;
                dc.DrawRoundedRectangle(PetWindow.ColorBrush("#30304B"), null,
                    new Rect(x, 72, 368, 304), 20, 20);
                PreviewText(dc, names[i], 13, "#E1DDF3", x + 17, 87);
                if (i > 0) window.Receive(Notice.Sample("Claude Code", Mood.Interrupted));
                window.Receive(Notice.Sample(i == 0 ? "Claude Code" : "Codex CLI", moods[i]));
                window.PreviewPose(0.34 + i * 0.11);
                window.Surface.UpdateLayout();
                var frame = new RenderTargetBitmap(364, 268, 96, 96, PixelFormats.Pbgra32);
                frame.Render(window.Surface);
                dc.DrawImage(frame, new Rect(x + 2, 113, 364, 268));
            }
        }
        SaveBitmap(composed, width, height, path);
    }

    private static void SavePlayPreview(PetWindow window, string path)
    {
        const int width = 1040;
        const int height = 522;
        var composed = new DrawingVisual();
        using (var dc = composed.RenderOpen())
        {
            dc.DrawRectangle(PetWindow.ColorBrush("#191A2C"), null, new Rect(0, 0, width, height));
            PreviewText(dc, "和巡巡玩", 24, "#FFF4D9", 24, 16);
            PreviewText(dc, "点击摸头，轻轻来回挠痒，停一会儿可以击掌", 13, "#C3BFDA", 149, 25);
            var actions = new[]
            {
                (Play.Pat, 0.37, PointerPose.None, "抚摸"),
                (Play.Feed, 0.20, PointerPose.None, "喂胡萝卜"),
                (Play.Fetch, 0.97, PointerPose.None, "抛球"),
                (Play.Dance, 0.15, PointerPose.None, "跳舞"),
                (Play.Nap, 0.92, PointerPose.None, "小睡"),
                (Play.None, 0.0, PointerPose.Sniff, "凑近闻闻"),
                (Play.Rub, 0.56, PointerPose.None, "挠痒"),
                (Play.None, 0.0, PointerPose.Paw, "邀请击掌"),
                (Play.HighFive, 0.53, PointerPose.None, "击掌")
            };
            for (var i = 0; i < actions.Length; i++)
            {
                var (activity, time, pointer, label) = actions[i];
                double x = 20 + i % 5 * 202;
                double y = i / 5 * 226;
                dc.DrawRoundedRectangle(PetWindow.ColorBrush("#30304B"), null,
                    new Rect(x, y + 62, 192, 214), 18, 18);
                PreviewText(dc, label, 13, "#E1DDF3", x + 13, y + 74);
                window.StartPlay(activity);
                window.PreviewPose(0.7, time, pointer);
                window.Surface.UpdateLayout();
                var frame = new RenderTargetBitmap(364, 302, 96, 96, PixelFormats.Pbgra32);
                frame.Render(window.Surface);
                var crop = new CroppedBitmap(frame, new Int32Rect(191, 119, 173, 181));
                dc.DrawImage(crop, new Rect(x + 10, y + 88, 172, 180));
            }
        }
        // Restore the idle pose so preview generation does not affect later checks.
        window.StartPlay(Play.None);
        SaveBitmap(composed, width, height, path);
    }

    private static void SaveBitmap(DrawingVisual composed, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(composed);
        using var file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    private static void PreviewText(DrawingContext context, string text, double size, string color, double x, double y)
    {
        var type = new Typeface("Microsoft YaHei UI");
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, type, size, PetWindow.ColorBrush(color), 1);
        context.DrawText(formatted, new Point(x, y));
    }

    private static void Require(bool expected, string message)
    {
        if (!expected) throw new InvalidOperationException(message);
    }
}
