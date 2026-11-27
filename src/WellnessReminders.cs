using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XunXun;

enum Habit { Water, Move }
enum HabitAction { Complete, Snooze, SkipToday }

sealed record HabitPlan(bool Enabled, int Minutes, DateOnly? SkipDay = null, double? SecondsLeft = null);
sealed record WellnessSettings(HabitPlan Water, HabitPlan Move)
{
    public static WellnessSettings Defaults { get; } = new(new(true, 45), new(true, 60));
}

sealed class WellnessSchedule
{
    internal const int SnoozeMinutes = 10;
    internal const int MaxMinutes = 720;
    private static readonly Habit[] Habits = [Habit.Water, Habit.Move];
    private readonly HabitPlan[] plans;
    private readonly double[] secondsLeft;

    public WellnessSchedule(WellnessSettings settings, DateOnly today)
    {
        var defaults = WellnessSettings.Defaults;
        plans = [Normalize(settings.Water, defaults.Water, today), Normalize(settings.Move, defaults.Move, today)];
        secondsLeft = [InitialSeconds(plans[0]), InitialSeconds(plans[1])];
    }

    private static HabitPlan Normalize(HabitPlan? plan, HabitPlan fallback, DateOnly today)
    {
        plan ??= fallback;
        plan = plan with { Minutes = Math.Clamp(plan.Minutes, 1, MaxMinutes) };
        return plan.SkipDay is { } skip && skip != today ? plan with { SkipDay = null, SecondsLeft = null } : plan;
    }

    private static double InitialSeconds(HabitPlan plan) => plan.SecondsLeft is { } seconds && double.IsFinite(seconds)
        ? Math.Clamp(seconds, 0, Math.Max(plan.Minutes, SnoozeMinutes) * 60)
        : plan.Minutes * 60;

    public HabitPlan Plan(Habit habit) => plans[(int)habit];

    public WellnessSettings Snapshot() => new(
        plans[0] with { SecondsLeft = secondsLeft[0] }, plans[1] with { SecondsLeft = secondsLeft[1] });

    public void Configure(Habit habit, bool enabled, int minutes)
    {
        int index = (int)habit;
        var previous = plans[index];
        minutes = Math.Clamp(minutes, 1, MaxMinutes);
        if (previous.Enabled == enabled && previous.Minutes == minutes) return;
        plans[index] = previous with { Enabled = enabled, Minutes = minutes };
        secondsLeft[index] = minutes * 60;
    }

    public Habit? Advance(TimeSpan elapsed, DateOnly today)
    {
        Habit? next = null;
        foreach (var habit in Habits)
        {
            int index = (int)habit;
            var plan = plans[index];
            if (plan.SkipDay == today) continue;
            if (plan.SkipDay is not null)
            {
                plans[index] = plan with { SkipDay = null };
                secondsLeft[index] = plan.Minutes * 60;
            }
            if (!plan.Enabled) continue;
            secondsLeft[index] = Math.Max(0, secondsLeft[index] - elapsed.TotalSeconds);
            if (secondsLeft[index] == 0) next ??= habit;
        }
        return next;
    }

    public void Resolve(Habit habit, HabitAction action, DateOnly today)
    {
        int index = (int)habit;
        if (action == HabitAction.SkipToday) plans[index] = plans[index] with { SkipDay = today };
        secondsLeft[index] = (action == HabitAction.Snooze ? SnoozeMinutes : plans[index].Minutes) * 60;
    }
}

sealed class WellnessReminders : IDisposable
{
    private readonly PetWindow owner;
    private readonly Func<bool> canShow;
    private readonly string file = Path.Combine(HookRegistration.AppDataDirectory, "wellness.json");
    private readonly WellnessSchedule schedule;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long lastTick = Stopwatch.GetTimestamp();
    private long shownAt;
    private bool wasActive;
    private bool settingsOpen;
    private double secondsSinceSave;
    private Habit? shown;
    private Window? popup;
    private Border? card;
    private TextBlock? caption;
    private TextBlock? countdown;

    public bool IsShowing => shown is not null || settingsOpen;

    public WellnessReminders(PetWindow owner, Func<bool> canShow)
    {
        this.owner = owner;
        this.canShow = canShow;
        schedule = new WellnessSchedule(LocalState.Read<WellnessSettings>(file) ?? WellnessSettings.Defaults, Today);
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    // Ignore the first tick after returning and long gaps during sleep or suspension.
    internal static TimeSpan ActiveElapsed(TimeSpan elapsed, bool wasActive, bool active) =>
        wasActive && active && elapsed < TimeSpan.FromSeconds(3) ? elapsed : TimeSpan.Zero;

    private void Tick()
    {
        long now = Stopwatch.GetTimestamp();
        bool active = DesktopActivity.IsActive();
        var gap = Stopwatch.GetElapsedTime(lastTick, now);
        var elapsed = ActiveElapsed(gap, wasActive, active);
        if (gap >= TimeSpan.FromSeconds(3)) SuspendPopup();
        lastTick = now;
        wasActive = active;
        var due = schedule.Advance(elapsed, Today);
        secondsSinceSave += elapsed.TotalSeconds;
        if (secondsSinceSave >= 60) Save();
        if (!active || !canShow() || settingsOpen || due is null)
        {
            SuspendPopup();
            return;
        }
        if (shown != due)
        {
            Show(due.Value, now);
            return;
        }
        int left = PetWindow.NoticeDurationSeconds - (int)Stopwatch.GetElapsedTime(shownAt, now).TotalSeconds;
        if (left <= 0) Resolve(HabitAction.Snooze);
        else countdown!.Text = $"{left} 秒后收起";
    }

    public void SuspendPopup()
    {
        if (shown is null) return;
        shown = null;
        popup?.Hide();
    }

    private void Show(Habit habit, long now)
    {
        if (popup is null) CreatePopup();
        shown = habit;
        shownAt = now;
        caption!.Text = habit == Habit.Water ? "喝口水吧" : "起来伸个懒腰";
        countdown!.Text = $"{PetWindow.NoticeDurationSeconds} 秒后收起";
        owner.PlaceFloatingCard(popup!, card!);
        if (!popup!.IsVisible) popup.Show();
        owner.PlayWellness(habit == Habit.Water ? Play.Drink : Play.Stretch);
    }

    private void CreatePopup()
    {
        var layout = new StackPanel();
        var header = new DockPanel();
        countdown = PetWindow.Label("", 11, "#BABAD3");
        DockPanel.SetDock(countdown, Dock.Right);
        header.Children.Add(countdown);
        caption = PetWindow.Label("", 18, "#FFFAEE", true);
        header.Children.Add(caption);
        layout.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, action) in new[] { ("完成", HabitAction.Complete),
            ("10 分钟后", HabitAction.Snooze), ("今天跳过", HabitAction.SkipToday) })
        {
            var button = PetWindow.Action(text, action == HabitAction.Complete);
            button.Margin = new Thickness(0, 0, 6, 0);
            button.Click += (_, _) => Resolve(action);
            buttons.Children.Add(button);
        }
        layout.Children.Add(buttons);
        card = new Border
        {
            Width = 314, Child = layout, Background = PetWindow.ColorBrush("#23233C"),
            BorderBrush = PetWindow.ColorBrush("#585474"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15), Padding = new Thickness(14)
        };
        popup = owner.CreateFloatingCard("巡巡 · 喝水和起身提醒", card);
    }

    private void Resolve(HabitAction action)
    {
        if (shown is not { } habit) return;
        schedule.Resolve(habit, action, Today);
        Save();
        SuspendPopup();
    }

    public void ShowSettings()
    {
        if (settingsOpen) return;
        settingsOpen = true;
        SuspendPopup();
        try
        {
            var body = new StackPanel();
            var water = AddSetting(body, "喝水", schedule.Plan(Habit.Water));
            var move = AddSetting(body, "起身活动", schedule.Plan(Habit.Move));
            var error = PetWindow.Label("", 12, "#FFC185");
            body.Children.Add(error);
            var save = PetWindow.Action("保存", true);
            save.IsDefault = true;
            var cancel = PetWindow.Action("取消", false);
            cancel.IsCancel = true;
            var dialog = HookSetupWindow.Create(owner, "巡巡 · 喝水和起身提醒", "喝水和起身提醒",
                "锁屏或连续 5 分钟无操作时暂停计时。", body, save, cancel);
            save.Click += (_, _) =>
            {
                if (!ValidMinutes(water.Minutes.Text, out int first) || !ValidMinutes(move.Minutes.Text, out int second))
                {
                    error.Text = $"请输入 1 至 {WellnessSchedule.MaxMinutes} 分钟。";
                    return;
                }
                schedule.Configure(Habit.Water, water.Enabled.IsChecked == true, first);
                schedule.Configure(Habit.Move, move.Enabled.IsChecked == true, second);
                Save();
                dialog.DialogResult = true;
            };
            dialog.ShowDialog();
        }
        finally { settingsOpen = false; }
    }

    private static (CheckBox Enabled, TextBox Minutes) AddSetting(StackPanel body, string title, HabitPlan plan)
    {
        var layout = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        var enabled = new CheckBox
        {
            IsChecked = plan.Enabled, Content = title, Foreground = PetWindow.ColorBrush("#F1F0FF"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var unit = PetWindow.Label("分钟", 12, "#D1D0E3");
        unit.VerticalAlignment = VerticalAlignment.Center;
        var minutes = new TextBox { Text = plan.Minutes.ToString(), Width = 62, Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetName(minutes, $"{title}提醒间隔");
        DockPanel.SetDock(unit, Dock.Right);
        DockPanel.SetDock(minutes, Dock.Right);
        layout.Children.Add(unit);
        layout.Children.Add(minutes);
        layout.Children.Add(enabled);
        body.Children.Add(layout);
        return (enabled, minutes);
    }

    private static bool ValidMinutes(string text, out int minutes) =>
        int.TryParse(text, out minutes) && minutes is >= 1 and <= WellnessSchedule.MaxMinutes;

    private void Save()
    {
        LocalState.Write(file, schedule.Snapshot());
        secondsSinceSave = 0;
    }

    public void Dispose()
    {
        timer.Stop();
        Save();
    }
}

static class DesktopActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder name, int bytes, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);

    public static bool IsActive()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        const uint idleMilliseconds = 5 * 60 * 1000;
        if (!GetLastInputInfo(ref input) || unchecked((uint)Environment.TickCount - input.Tick) >= idleMilliseconds)
            return false;

        const uint desktopReadObjects = 0x0001;
        var desktop = OpenInputDesktop(0, false, desktopReadObjects);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            const int nameIndex = 2;
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, nameIndex, name, name.Capacity * sizeof(char), out _)
                && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
}
