using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XunXun;

sealed class TaskInbox
{
    private const int Limit = 100;
    private readonly string file;
    private readonly Dictionary<(string Provider, string Session, bool Completed), Entry> entries = new();
    private Window? window;
    private StackPanel? rows;

    internal sealed record Entry(Notice Item, DateTimeOffset ReceivedAt);

    public TaskInbox() : this(Path.Combine(HookRegistration.AppDataDirectory, "task-inbox.json")) { }

    internal TaskInbox(string file)
    {
        this.file = file;
        foreach (var entry in (LocalState.Read<Entry[]>(file) ?? [])
            .Where(entry => entry?.Item is { Demo: false, Mood: Mood.Approval or Mood.Question or Mood.Done,
                Provider: "Claude Code" or "Codex CLI" or T3Completion.Provider, Session.Length: > 0 })
            .OrderByDescending(entry => entry.ReceivedAt).Take(Limit))
            entries.TryAdd(Key(entry.Item, entry.Item.Mood == Mood.Done), entry);
    }

    public Entry[] Entries => entries.Values.OrderBy(entry => entry.Item.Mood)
        .ThenByDescending(entry => entry.ReceivedAt).ToArray();

    public bool IsOpen => window is { IsVisible: true };

    public void Observe(Notice item, DateTimeOffset receivedAt)
    {
        if (item.Demo) return;
        var pending = Key(item, false);
        switch (item.Mood)
        {
            case Mood.Approval:
            case Mood.Question:
                entries[pending] = new Entry(item, receivedAt);
                break;
            case Mood.Done:
                entries.Remove(pending);
                entries[Key(item, true)] = new Entry(item, receivedAt);
                break;
            case Mood.Interrupted:
            case Mood.Ended:
                if (!entries.Remove(pending)) return;
                break;
            case Mood.PromptResolved:
                if (!entries.TryGetValue(pending, out var existing) || existing.Item.Tool != item.Tool) return;
                entries.Remove(pending);
                break;
            default:
                return;
        }

        foreach (var key in entries.OrderByDescending(pair => pair.Value.ReceivedAt).Skip(Limit)
            .Select(pair => pair.Key).ToArray())
            entries.Remove(key);
        LocalState.Write(file, Entries);
        Render();
    }

    public void Show(Window owner)
    {
        if (window is { IsVisible: true })
        {
            window.Activate();
            return;
        }

        var body = new StackPanel();
        rows = new StackPanel();
        body.Children.Add(new ScrollViewer
        {
            Content = rows, MaxHeight = 410,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var close = PetWindow.Action("关闭", true);
        var dialog = HookSetupWindow.Create(owner, "巡巡 · 任务收件箱", "任务收件箱",
            null, body, close, width: 500);
        window = dialog;
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => { window = null; rows = null; };
        Render();
        dialog.Show();
    }

    private void Render()
    {
        if (rows is null) return;
        rows.Children.Clear();
        if (entries.Count == 0)
        {
            rows.Children.Add(PetWindow.Label("暂无任务提醒", 13, "#D1D0E3"));
            return;
        }

        foreach (var entry in Entries)
        {
            var item = entry.Item;
            var (title, color) = item.Mood switch
            {
                Mood.Approval => ("待审批", "#FFC185"),
                Mood.Question => ("待回答", "#CFC0FF"),
                _ => ("已完成", "#8FF2D0")
            };
            var layout = new DockPanel();
            var open = PetWindow.Action("回到工作区 ↗", true);
            open.IsEnabled = TaskTarget.Available(item);
            open.Margin = new Thickness(12, 0, 0, 0);
            open.VerticalAlignment = VerticalAlignment.Center;
            if (!open.IsEnabled) open.ToolTip = "这条提醒没有绑定工作区窗口。";
            DockPanel.SetDock(open, Dock.Right);
            layout.Children.Add(open);

            var labels = new StackPanel();
            labels.Children.Add(PetWindow.Label($"{title}  ·  {item.Provider}", 13, color, true));
            string project = string.IsNullOrWhiteSpace(item.Project) ? "会话提醒" : item.Project;
            var summary = PetWindow.Label(project, 12, "#F1F0FF");
            summary.Margin = new Thickness(0, 3, 0, 0);
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            summary.ToolTip = project;
            labels.Children.Add(summary);
            string time = entry.ReceivedAt.ToLocalTime().ToString("MM-dd HH:mm");
            if (item.Mood != Mood.Done && !string.IsNullOrWhiteSpace(item.Tool)) time += $"  ·  {item.Tool}";
            var timestamp = PetWindow.Label(time, 11, "#B8B8D1");
            timestamp.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(timestamp);
            var error = PetWindow.Label("", 11, "#FFC185");
            error.TextWrapping = TextWrapping.Wrap;
            error.Visibility = Visibility.Collapsed;
            labels.Children.Add(error);
            open.Click += (_, _) =>
            {
                if (TaskTarget.TryOpen(item, out var reason)) window?.Close();
                else { error.Text = reason; error.Visibility = Visibility.Visible; }
            };
            layout.Children.Add(labels);
            rows.Children.Add(new Border
            {
                Child = layout, Background = PetWindow.ColorBrush("#302E49"), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 8, 11, 8), Margin = new Thickness(0, 0, 0, 7)
            });
        }
    }

    private static (string Provider, string Session, bool Completed) Key(Notice item, bool completed) =>
        (item.Provider, item.Session, completed);
}
