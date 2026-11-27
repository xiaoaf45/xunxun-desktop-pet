using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XunXun;

sealed class DailyBriefing : IDisposable
{
    private readonly PetWindow pet;
    private readonly TaskInbox inbox;
    private readonly Func<bool> canShow;
    private readonly Action openInbox;
    private readonly BriefingClient client;
    private readonly string optionsFile;
    private readonly string stateFile;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? request;
    private BriefingOptions options;
    private BriefingState state;
    private Window? viewer;
    private StackPanel? sections;
    private Button? refreshButton;
    private TextBlock? status;
    private Window? popup;
    private long popupOpenedAt;
    private bool editing;
    private bool disposed;

    public DailyBriefing(PetWindow pet, TaskInbox inbox, Func<bool> canShow, Action openInbox,
        BriefingClient? client = null)
    {
        this.pet = pet;
        this.inbox = inbox;
        this.canShow = canShow;
        this.openInbox = openInbox;
        this.client = client ?? new BriefingClient();
        string folder = HookRegistration.AppDataDirectory;
        optionsFile = Path.Combine(folder, "briefing-options.json");
        stateFile = Path.Combine(folder, "briefing-cache.json");
        options = (LocalState.Read<BriefingOptions>(optionsFile) ?? new()).Normalize();
        state = LocalState.Read<BriefingState>(stateFile) ?? new();
        timer.Tick += (_, _) =>
        {
            if (options.Enabled || options.Recap) Tick(DateTimeOffset.Now, DesktopActivity.IsActive());
        };
        timer.Start();
    }

    internal void Tick(DateTimeOffset now, bool active)
    {
        if (disposed) return;
        bool available = active && canShow() && viewer is not { IsVisible: true };
        if (popup is not null)
        {
            if (!available || Stopwatch.GetElapsedTime(popupOpenedAt).TotalSeconds >= PetWindow.NoticeDurationSeconds)
                SuspendPopup();
            return;
        }
        if (!available) return;

        var day = BriefingSchedule.Day(now);
        if (BriefingSchedule.IsDue(options.Recap, options.RecapTime, state.RecapDay, now))
        {
            state = state with { RecapDay = day };
            SaveState();
            var recap = TaskRecap.From(inbox.Entries, now);
            if (recap.HasWork)
            {
                Announce("今日任务小结", recap.Copy, "打开任务收件箱", openInbox);
                return;
            }
        }

        if (!BriefingSchedule.IsDue(options.Enabled, options.Time, state.ShownDay, now)
            || request is not null || !options.Sources().Any()) return;
        if (state.FetchDay != day)
        {
            _ = Refresh(now, automatic: true);
            return;
        }
        var note = BriefingSchedule.Build(options, state, now);
        state = state with { ShownDay = day, Seen = note.Seen };
        SaveState();
        if (note.Copy.Length > 0)
            Announce("每日简报", note.Copy, "查看简报", Show, note.Rain ? Play.Umbrella : Play.Newspaper);
    }

    internal async Task Refresh(DateTimeOffset now, bool automatic = false)
    {
        if (disposed || request is not null) return;
        var current = options;
        var sources = current.Sources().ToArray();
        if (sources.Length == 0) return;
        using var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request = next;
        Render();
        try
        {
            var results = await Task.WhenAll(sources.Select(source => client.Fetch(source, current, now, next.Token)));
            if (next.IsCancellationRequested || disposed) return;
            state = state.WithResults(results);
            if (automatic) state = state with { FetchDay = BriefingSchedule.Day(now) };
            SaveState();
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested) { }
        finally
        {
            request = null;
            if (!disposed) Render();
        }
    }

    public void Show()
    {
        SuspendPopup();
        if (viewer is { IsVisible: true })
        {
            viewer.Activate();
            return;
        }
        var body = new StackPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var configure = PetWindow.Action("订阅设置", false);
        configure.Click += (_, _) => Configure();
        actions.Children.Add(configure);
        refreshButton = PetWindow.Action("刷新", false);
        refreshButton.Margin = new Thickness(8, 0, 0, 0);
        refreshButton.Click += async (_, _) => await Refresh(DateTimeOffset.Now);
        actions.Children.Add(refreshButton);
        body.Children.Add(actions);
        status = PetWindow.Label("", 12, "#BCBAD3");
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(status);
        sections = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = sections, MaxHeight = 405,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var close = PetWindow.Action("关闭", true);
        var dialog = HookSetupWindow.Create(pet, "巡巡 · 每日简报", "每日简报", null, body, close, width: 565);
        viewer = dialog;
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => { viewer = null; sections = null; status = null; refreshButton = null; };
        Render();
        dialog.Show();
    }

    private void Configure()
    {
        if (editing || viewer is null) return;
        editing = true;
        try
        {
            var updated = BriefingSettingsWindow.Edit(viewer, options, client, lifetime.Token);
            if (updated is null || updated == options) return;
            bool changedSources = !options.Sources().Select(options.Key).SequenceEqual(updated.Sources().Select(updated.Key));
            options = updated;
            LocalState.Write(optionsFile, options);
            if (changedSources)
            {
                request?.Cancel();
                if (state.ShownDay != BriefingSchedule.Day(DateTimeOffset.Now))
                {
                    state = state with { FetchDay = null };
                    SaveState();
                }
            }
            Render();
        }
        finally { editing = false; }
    }

    private void Render()
    {
        if (sections is null || status is null || refreshButton is null) return;
        var sources = options.Sources().ToArray();
        refreshButton.IsEnabled = request is null && sources.Length > 0;
        status.Text = request is not null ? "正在更新订阅…"
            : sources.Length == 0 ? "可在订阅设置中选择栏目。"
            : options.Enabled ? $"自动推送  {options.Time}" : "自动推送已关闭，可手动刷新";
        sections.Children.Clear();

        if (options.Weather)
        {
            var weather = Section("出门天气");
            if (options.City is null) Message(weather, "请在订阅设置中选择城市。");
            else AddSource(weather, BriefingSource.Weather);
        }
        if (options.Releases)
        {
            var releases = Section("工具正式版");
            AddSource(releases, BriefingSource.Claude);
            AddSource(releases, BriefingSource.Codex);
        }
        if (options.Github)
        {
            var repositories = Section($"GitHub · {options.Topic}");
            Message(repositories, "近 7 个 UTC 日新建，按累计星标排序 · 最多 3 个");
            AddSource(repositories, BriefingSource.Github);
        }
        if (!options.News) return;

        var news = Section("前沿 AI 资讯");
        var feeds = sources.Where(BriefingClient.IsNews).ToArray();
        var snapshots = feeds.Select(source => state.Find(options, source)).OfType<BriefingSnapshot>().ToArray();
        var now = DateTimeOffset.Now;
        var articles = BriefingSchedule.News(snapshots, options.Keywords, now);
        if (snapshots.Length > 0)
            Message(news, $"最近抓取 {snapshots.Max(source => source.FetchedAt).ToLocalTime():MM-dd HH:mm}");
        foreach (var article in articles)
            Item(news, article.Source, article.Item, $"{BriefingClient.SourceName(article.Source)}  ·  {article.Item.Published:MM-dd}");
        foreach (var source in feeds)
        {
            if (state.Failure(options, source) is not { } failure) continue;
            var cache = state.Find(options, source);
            Message(news, $"{BriefingClient.SourceName(source)} · {failure.Message}" + (cache is null ? ""
                : $" · 缓存 {cache.FetchedAt.ToLocalTime():MM-dd HH:mm}"));
        }
        if (articles.Length == 0)
            Message(news, snapshots.Length > 0 ? "近 7 天暂无符合筛选条件的资讯。" : "刷新后可查看资讯。");
    }

    private StackPanel Section(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var heading = PetWindow.Label(title, 15, "#CFC0FF", true);
        heading.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(heading);
        sections!.Children.Add(panel);
        return panel;
    }

    private void AddSource(StackPanel section, BriefingSource source)
    {
        var cache = state.Find(options, source);
        var failure = state.Failure(options, source);
        string name = BriefingClient.SourceName(source);
        if (cache is null)
        {
            Message(section, $"{name} · {failure?.Message ?? "等待读取"}");
            return;
        }
        string heading = failure is null ? "更新" : $"{failure.Message}，显示缓存";
        Message(section, $"{name} · {heading} {cache.FetchedAt.ToLocalTime():MM-dd HH:mm}");
        var items = BriefingSchedule.Rows(cache).ToArray();
        foreach (var row in items) Item(section, row.Source, row.Item, null);
        if (items.Length == 0) Message(section, "暂无条目");
    }

    private void Item(StackPanel section, BriefingSource source, BriefingItem item, string? meta)
    {
        var body = new StackPanel();
        var title = PetWindow.Label(item.Title, 13, "#F1F0FF", true);
        title.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(title);
        if (meta is not null) Message(body, meta);
        if (item.Detail.Length > 0) Message(body, item.Detail);
        var error = PetWindow.Label("", 11, "#FFC185");
        error.Visibility = Visibility.Collapsed;
        body.Children.Add(error);
        var open = PetWindow.Action(source == BriefingSource.Weather ? "来源 ↗" : "打开 ↗", false);
        open.VerticalAlignment = VerticalAlignment.Center;
        open.Margin = new Thickness(8, 0, 0, 0);
        open.Click += (_, _) =>
        {
            if (!BriefingClient.CanOpen(source, item.Url)) return;
            try { using var process = Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
            catch (Win32Exception) { error.Text = "无法打开来源"; error.Visibility = Visibility.Visible; }
        };
        var layout = new DockPanel();
        DockPanel.SetDock(open, Dock.Right);
        layout.Children.Add(open);
        layout.Children.Add(body);
        section.Children.Add(new Border
        {
            Child = layout, Background = PetWindow.ColorBrush("#302E49"), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 3, 0, 0)
        });
    }

    private static void Message(Panel parent, string text)
    {
        var label = PetWindow.Label(text, 11, "#CBC9E0");
        label.TextWrapping = TextWrapping.Wrap;
        label.Margin = new Thickness(0, 3, 0, 2);
        parent.Children.Add(label);
    }

    private void Announce(string title, string copy, string action, Action open, Play animation = Play.None)
    {
        var body = new StackPanel();
        body.Children.Add(PetWindow.Label(title, 17, "#FFFAEE", true));
        var text = PetWindow.Label(copy, 12, "#D1D0E3");
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 7, 0, 9);
        body.Children.Add(text);
        var go = PetWindow.Action(action, true);
        go.HorizontalAlignment = HorizontalAlignment.Left;
        go.Click += (_, _) => { SuspendPopup(); open(); };
        body.Children.Add(go);
        var card = new Border
        {
            Child = body, Width = 325, Padding = new Thickness(14), CornerRadius = new CornerRadius(15),
            BorderBrush = PetWindow.ColorBrush("#585474"), BorderThickness = new Thickness(1),
            Background = PetWindow.ColorBrush("#23233C")
        };
        var notice = pet.CreateFloatingCard("巡巡 · " + title + "提醒", card);
        popup = notice;
        notice.Closed += (_, _) => { if (popup == notice) popup = null; };
        popupOpenedAt = Stopwatch.GetTimestamp();
        pet.PlaceFloatingCard(notice, card);
        notice.Show();
        if (animation != Play.None) pet.PlayReminder(animation);
    }

    public void SuspendPopup()
    {
        var window = popup;
        popup = null;
        window?.Close();
    }

    private void SaveState() => LocalState.Write(stateFile, state);

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
