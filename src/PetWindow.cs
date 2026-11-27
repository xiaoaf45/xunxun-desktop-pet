using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace XunXun;

sealed class PetWindow : Window
{
    private readonly Dictionary<(string, string, bool), (Notice Item, DateTime Seen)> sessions = new();
    private readonly Dictionary<string, Window> demoWindows = new();
    private readonly PetArt pet = new();
    private readonly DesktopRoam roam = new();
    private readonly RoamTricks roamTricks = new();
    private readonly ScaleTransform petScale = new(1, 1, 70, 73);
    private const double AnchorX = 288;
    private const double AnchorY = 219;
    private const int NoticeDurationSeconds = 10;
    private readonly Border card;
    private readonly bool preview;
    private Window? noticeWindow;
    private readonly Border playTray;
    private readonly Button napButton;
    private readonly TextBlock category = Label("", 12, "#FFC185", true);
    private readonly TextBlock source = Label("", 11, "#BABAD3");
    private readonly TextBlock countdown = Label("", 11, "#BABAD3");
    private readonly TextBlock headline = Label("", 18, "#FEF8EC", true);
    private readonly TextBlock detail = Label("", 12, "#CECEE1");
    private readonly Button open = Action("回到任务  ↗", true);
    private readonly Button next = Action("", false);
    private readonly DispatcherTimer animation;
    private readonly DispatcherTimer firstDemo;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Forms.NotifyIcon? tray;
    private readonly System.Drawing.Icon? trayImage;
    private static readonly Uri IconResource = new("pack://application:,,,/Assets/xunxun.ico");
    private Notice? current;
    private double? noticeClosesAt;
    private int countdownSeconds;
    private int selection;
    private bool quiet;
    private bool launcherOpen;
    private double playingSince;
    private double offerPlayUntil = 4.2;
    private double lastFrame;
    private double lastHover;
    private double lastStroke;
    private double strokeX;
    private int strokeDirection;
    private int strokes;
    private double nextWorkAreaRead;
    private double nextIconsRead;
    private Task<Rect[]>? iconsRead;
    private Rect[] iconPixels = Array.Empty<Rect>();
    private Rect[] icons = Array.Empty<Rect>();
    private DesktopRoam.Display[] displays = [];
    private Rect workArea;
    private Point press;
    private Vector dragOffset;
    private Play pressAction;
    private HighFivePointer? highFivePointer;
    private bool dragging;
    private int demoStage;
    private DateTime cleanupAt;

    public FrameworkElement Surface { get; }
    public Button OpenButton => open;
    public Notice? Current => current;
    internal Play CurrentPlay => pet.Activity;

    public PetWindow(bool preview = false)
    {
        this.preview = preview;
        Width = 364;
        Height = 302;
        Title = "巡巡 · AI 桌宠";
        Icon = BitmapFrame.Create(IconResource);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = !preview;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var canvas = new Canvas { Width = Width, Height = Height, Background = Brushes.Transparent };
        Surface = canvas;
        card = new Border
        {
            Width = 342, Padding = new Thickness(15, 12, 13, 13),
            CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1),
            BorderBrush = ColorBrush("#555473"), Background = ColorBrush("#23233C")
        };
        var content = new StackPanel();
        var top = new DockPanel { LastChildFill = false };
        var close = Action("×", false);
        close.ToolTip = "收起这条提醒";
        close.Click += (_, _) => Dismiss();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        countdown.Margin = new Thickness(0, 0, 8, 0);
        countdown.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(countdown, Dock.Right);
        top.Children.Add(countdown);
        top.Children.Add(category);
        source.Margin = new Thickness(10, 0, 0, 0);
        source.VerticalAlignment = VerticalAlignment.Center;
        category.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(source);
        content.Children.Add(top);
        headline.Margin = new Thickness(0, 5, 0, 3);
        content.Children.Add(headline);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(detail);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 11, 0, 0) };
        controls.Children.Add(open);
        next.Margin = new Thickness(7, 0, 0, 0);
        controls.Children.Add(next);
        next.Click += (_, _) => { selection++; Refresh(); };
        open.Click += (_, _) => Jump();
        content.Children.Add(controls);
        card.Child = content;
        if (preview)
        {
            Canvas.SetLeft(card, 8);
            Canvas.SetTop(card, 4);
            canvas.Children.Add(card);
        }

        Canvas.SetLeft(pet, 218);
        Canvas.SetTop(pet, 146);
        pet.RenderTransform = petScale;
        canvas.Children.Add(pet);
        var playButtons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (title, tip, activity) in new[]
        {
            ("喂食", "丢一根胡萝卜", Play.Feed),
            ("抛球", "和巡巡玩接球", Play.Fetch),
            ("跳舞", "让巡巡跳支舞", Play.Dance),
            ("小睡", "小睡或叫醒巡巡", Play.Nap)
        })
        {
            var button = Action(title, false);
            button.FontSize = 11;
            button.Padding = new Thickness(3.5, 4, 3.5, 4);
            button.Margin = new Thickness(1);
            button.ToolTip = tip;
            button.Click += (_, _) => StartPlay(activity);
            playButtons.Children.Add(button);
        }
        napButton = (Button)playButtons.Children[3];
        playTray = new Border
        {
            Child = playButtons, Padding = new Thickness(3), CornerRadius = new CornerRadius(11),
            Background = ColorBrush("#23233C"), BorderBrush = ColorBrush("#555473"),
            BorderThickness = new Thickness(1), Visibility = preview ? Visibility.Collapsed : Visibility.Visible
        };
        Canvas.SetLeft(playTray, 218);
        Canvas.SetTop(playTray, 263);
        canvas.Children.Add(playTray);
        Content = canvas;

        var menu = new ContextMenu();
        AddMenu(menu, "启动 Claude / Codex", OpenLauncher);
        AddMenu(menu, "播放提醒演示", StartDemo);
        AddMenu(menu, "设置任务提醒", () => HookRegistration.Offer(this, manual: true));
        var mute = new MenuItem { Header = "免打扰", IsCheckable = true };
        mute.Click += (_, _) => SetQuiet(mute.IsChecked);
        menu.Items.Add(mute);
        menu.Opened += (_, _) => mute.IsChecked = quiet;
        AddMenu(menu, "退出巡巡", () => Application.Current.Shutdown());
        pet.ContextMenu = menu;
        pet.MouseEnter += (_, eventArgs) =>
        {
            lastHover = clock.Elapsed.TotalSeconds;
            strokes = 0;
            strokeDirection = 0;
            strokeX = eventArgs.GetPosition(pet).X;
            RevealPlay();
        };
        pet.MouseLeave += (_, _) => OfferPlayBriefly();
        playTray.MouseEnter += (_, _) => RevealPlay();
        playTray.MouseLeave += (_, _) => OfferPlayBriefly();
        pet.MouseLeftButtonDown += (_, eventArgs) =>
        {
            ReleaseHighFivePointer();
            if (eventArgs.ClickCount == 2)
            {
                eventArgs.Handled = true;
                OpenLauncher();
                return;
            }
            press = eventArgs.GetPosition(this);
            dragOffset = PointToScreen(press) - PointToScreen(new Point());
            pressAction = pet.Activity == Play.Nap ? Play.Wake
                : pet.PointerPose == PointerPose.Paw ? Play.HighFive : Play.Pat;
            dragging = false;
            pet.CaptureMouse();
            pet.Cursor = PetCursors.Pressed;
        };
        pet.MouseMove += (_, eventArgs) =>
        {
            var position = eventArgs.GetPosition(pet);
            if (eventArgs.LeftButton == MouseButtonState.Released)
            {
                CheckRub(position.X);
                return;
            }
            if (!pet.IsMouseCaptured) return;
            if (!dragging)
            {
                var shift = eventArgs.GetPosition(this) - press;
                if (Math.Abs(shift.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(shift.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragging = true;
                StartPlay(Play.None);
                pet.IsBeingDragged = true;
                pet.InvalidateVisual();
            }
            var cursor = Forms.Cursor.Position;
            var windowOrigin = DesktopPoint(new Point(cursor.X, cursor.Y) - dragOffset);
            Left = windowOrigin.X;
            Top = windowOrigin.Y;
        };
        pet.MouseLeftButtonUp += (_, _) =>
        {
            bool pickedUp = pet.IsMouseCaptured;
            bool dragged = dragging;
            pet.ReleaseMouseCapture();
            pet.Cursor = PetCursors.Open;
            if (!pickedUp || dragged) return;
            StartPlay(pressAction);
            if (pressAction == Play.HighFive)
            {
                var cursor = Forms.Cursor.Position;
                highFivePointer = new HighFivePointer(new Point(cursor.X, cursor.Y),
                    pet.PointToScreen(pet.HighFiveContact(0)));
                pet.Cursor = PetCursors.Pressed;
            }
        };
        pet.LostMouseCapture += (_, _) =>
        {
            pet.Cursor = PetCursors.Open;
            if (!dragging) return;
            dragging = false;
            pet.IsBeingDragged = false;
            pet.InvalidateVisual();
            nextWorkAreaRead = 0;
            roam.Reset(clock.Elapsed.TotalSeconds);
        };
        Deactivated += (_, _) => ReleaseHighFivePointer();

        var work = SystemParameters.WorkArea;
        workArea = work;
        Left = work.Right - Width - 26;
        Top = work.Bottom - Height - 13;
        animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        animation.Tick += (_, _) => Animate();
        if (!preview) animation.Start();
        firstDemo = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        firstDemo.Tick += (_, _) => { firstDemo.Stop(); DemoTo(1); };
        Closed += (_, _) =>
        {
            animation.Stop();
            firstDemo.Stop();
            if (tray is not null) { tray.Visible = false; tray.Dispose(); }
            trayImage?.Dispose();
        };
        if (!preview)
        {
            using var stream = Application.GetResourceStream(IconResource).Stream;
            using var image = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            trayImage = (System.Drawing.Icon)image.Clone();
            tray = TrayIcon(trayImage);
        }
        Refresh();
    }

    private Forms.NotifyIcon TrayIcon(System.Drawing.Icon image)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("启动 Claude / Codex", null, (_, _) => OpenLauncher());
        menu.Items.Add("播放提醒演示", null, (_, _) => StartDemo());
        menu.Items.Add("设置任务提醒", null, (_, _) => HookRegistration.Offer(this, manual: true));
        var mute = new Forms.ToolStripMenuItem("免打扰") { CheckOnClick = true };
        mute.CheckedChanged += (_, _) => SetQuiet(mute.Checked);
        menu.Opening += (_, _) => mute.Checked = quiet;
        menu.Items.Add(mute);
        menu.Items.Add("退出巡巡", null, (_, _) => Application.Current.Shutdown());
        return new Forms.NotifyIcon
        {
            Icon = image,
            Text = "巡巡 · AI 桌宠",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    private void OpenLauncher()
    {
        if (launcherOpen) return;
        launcherOpen = true;
        try { HookSetupWindow.Launch(this); }
        finally { launcherOpen = false; }
    }

    public void Receive(Notice item)
    {
        var key = (item.Provider, item.Session, item.Demo);
        if (item.Mood is Mood.Approval or Mood.Question or Mood.Done)
            sessions[key] = (item, DateTime.UtcNow);
        else if (sessions.TryGetValue(key, out var last)
            && (item.Mood == Mood.Interrupted
                || item.Mood == Mood.Ended && last.Item.Mood != Mood.Done
                || item.Mood == Mood.PromptResolved && last.Item.Mood != Mood.Done && last.Item.Tool == item.Tool))
            sessions.Remove(key);
        else return;
        selection = 0;
        Refresh();
    }

    private void Refresh()
    {
        var pending = sessions.Values.OrderBy(pair => pair.Seen).Select(pair => pair.Item).ToArray();
        var previous = current;
        current = pending.Length == 0 ? null : pending[selection % pending.Length];
        card.Visibility = quiet || current is null ? Visibility.Collapsed : Visibility.Visible;
        if (card.Visibility != Visibility.Visible)
        {
            noticeClosesAt = null;
            noticeWindow?.Hide();
            return;
        }
        bool newlyShown = noticeClosesAt is null || current != previous;
        if (newlyShown)
        {
            noticeClosesAt = clock.Elapsed.TotalSeconds + NoticeDurationSeconds;
            SetCountdown(NoticeDurationSeconds);
        }
        if (current is not { } item) return;
        var (tag, title, accent) = item.Mood switch
        {
            Mood.Approval => ("待审批", "有一步需要你确认", "#FFC185"),
            Mood.Question => ("等回复", "有个问题等你回答", "#CFC0FF"),
            _ => ("已结束", "这一轮工作结束了", "#8FF2D0")
        };
        category.Text = tag;
        category.Foreground = ColorBrush(accent);
        headline.Text = title;
        source.Text = item.Demo ? item.Provider + "  ·  演示" : item.Provider;
        detail.Text = item.Demo ? item.Mood switch
        {
            Mood.Approval => "组件文档 · 等待确认是否运行检查",
            Mood.Question => "测试工作区 · 请选择要验证的范围",
            _ => "测试工作区 · 可以回去查看结果"
        } : string.IsNullOrEmpty(item.Project) ? "从已接入会话收到的提醒" : item.Project;
        bool t3 = item.Provider == T3Completion.Provider;
        bool linked = item.Demo || item.Window != 0 || t3 && T3Completion.ThreadLink(item.Link) is not null;
        open.IsEnabled = linked;
        open.Content = item.Demo ? "回到任务  ↗"
            : t3 && linked ? "回到 T3 Code  ↗" : linked ? "切到任务窗口  ↗" : "未绑定窗口";
        next.Content = $"下一条  {pending.Length - 1}";
        next.Visibility = pending.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (!preview && newlyShown) ShowNoticeWindow();
    }

    private void ShowNoticeWindow()
    {
        noticeWindow ??= new Window
        {
            Owner = this, Title = "巡巡 · 任务提醒", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, ShowActivated = false,
            Topmost = Topmost, FontFamily = FontFamily, UseLayoutRounding = true, Content = card
        };
        card.Measure(new Size(card.Width, double.PositiveInfinity));
        noticeWindow.Left = Math.Clamp(Left + 8, workArea.Left,
            Math.Max(workArea.Left, workArea.Right - card.Width));
        double maxTop = Math.Max(workArea.Top, workArea.Bottom - card.DesiredSize.Height);
        noticeWindow.Top = Math.Clamp(Top + 4, workArea.Top, maxTop);
        if (!noticeWindow.IsVisible) noticeWindow.Show();
    }

    private void SetQuiet(bool value)
    {
        quiet = value;
        Refresh();
        if (tray is not null) tray.Text = quiet ? "巡巡 · 免打扰" : "巡巡 · AI 桌宠";
    }

    private void Animate()
    {
        double elapsed = clock.Elapsed.TotalSeconds;
        if (noticeClosesAt is { } deadline)
        {
            int remaining = (int)Math.Ceiling(deadline - elapsed);
            if (remaining <= 0) Dismiss();
            else SetCountdown(remaining);
        }
        double frame = Math.Clamp(elapsed - lastFrame, 0, 0.075);
        lastFrame = elapsed;
        pet.Time = elapsed;
        pet.PlayTime = elapsed - playingSince;
        if (pet.Activity != Play.None && pet.PlayTime >= pet.Activity.Duration())
            pet.Activity = Play.None;
        if (elapsed >= offerPlayUntil && pet.Activity == Play.None)
            playTray.Visibility = Visibility.Collapsed;
        MoveHighFivePointer();
        AdvanceMotion(Forms.Cursor.Position, elapsed, frame);
        pet.InvalidateVisual();
        if (DateTime.UtcNow < cleanupAt) return;
        cleanupAt = DateTime.UtcNow.AddSeconds(30);
        var expired = sessions.Where(pair => !pair.Value.Item.Demo
            && DateTime.UtcNow - pair.Value.Seen > TimeSpan.FromHours(2))
            .Select(pair => pair.Key).ToArray();
        foreach (var key in expired) sessions.Remove(key);
        if (expired.Length != 0) Refresh();
    }

    private void SetCountdown(int seconds)
    {
        if (countdownSeconds == seconds) return;
        countdownSeconds = seconds;
        countdown.Text = $"{seconds} 秒后关闭";
    }

    internal Point ScreenAnchor => PointToScreen(new Point(AnchorX, AnchorY));

    internal void PlaceAnchor(Point pixel, double elapsed)
    {
        var next = DesktopPoint(pixel);
        Left = next.X - AnchorX;
        Top = next.Y - AnchorY;
        nextWorkAreaRead = 0;
        roam.Reset(elapsed);
    }

    internal void AdvanceMotion(System.Drawing.Point cursor, double elapsed, double frame)
    {
        var point = ScreenAnchor;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
        UpdateDesktop(elapsed, screen);
        UpdatePointerAndWalk(cursor, elapsed, frame, screen);
    }

    private void MoveHighFivePointer()
    {
        if (highFivePointer is not { } gesture) return;
        var cursor = Forms.Cursor.Position;
        var current = new Point(cursor.X, cursor.Y);
        if (pet.Activity != Play.HighFive || !IsActive || Mouse.LeftButton != MouseButtonState.Released
            || !gesture.TryFollow(current, pet.PointToScreen(pet.HighFiveContact(pet.PlayTime)),
                pet.PlayTime, out var target))
        {
            ReleaseHighFivePointer();
            return;
        }

        if (target != current)
            Forms.Cursor.Position = new System.Drawing.Point((int)target.X, (int)target.Y);
    }

    private void ReleaseHighFivePointer()
    {
        if (highFivePointer is null) return;
        highFivePointer = null;
        pet.Cursor = PetCursors.Open;
    }

    private void UpdateDesktop(double elapsed, Forms.Screen screen)
    {
        bool refreshed = iconsRead is { IsCompleted: true };
        if (refreshed)
        {
            if (iconsRead!.IsCompletedSuccessfully) iconPixels = iconsRead.Result;
            else _ = iconsRead.Exception;
            iconsRead = null;
        }
        if (elapsed >= nextIconsRead && iconsRead is null)
        {
            nextIconsRead = elapsed + 4;
            iconsRead = Task.Run(DesktopIcons.Read);
        }
        workArea = DesktopRect(screen.WorkingArea);
        if (!refreshed && elapsed < nextWorkAreaRead) return;
        nextWorkAreaRead = elapsed + 1;
        icons = iconPixels.Select(box => new Rect(DesktopPoint(box.TopLeft), DesktopPoint(box.BottomRight))).ToArray();
        displays = Forms.Screen.AllScreens.Select(display => new DesktopRoam.Display(display.DeviceName,
            DesktopRect(display.Bounds), WalkBounds(DesktopRect(display.WorkingArea)))).ToArray();
    }

    private Point DesktopPoint(Point pixel)
    {
        var local = PointFromScreen(pixel);
        return new Point(Left + local.X, Top + local.Y);
    }

    private Rect DesktopRect(System.Drawing.Rectangle pixels) =>
        new(DesktopPoint(new Point(pixels.Left, pixels.Top)), DesktopPoint(new Point(pixels.Right, pixels.Bottom)));

    private static Rect WalkBounds(Rect work) =>
        new(work.Left + Math.Min(65, work.Width / 4), work.Top + Math.Min(71, work.Height / 4),
            Math.Max(1, work.Width - 130), Math.Max(1, work.Height - 155));

    private void UpdatePointerAndWalk(System.Drawing.Point cursor, double elapsed, double frame, Forms.Screen screen)
    {
        var pointer = DesktopPoint(new Point(cursor.X, cursor.Y));
        var position = new Point(Left + AnchorX, Top + AnchorY);
        Vector towardsPointer = pointer - position;
        bool near = towardsPointer.LengthSquared < 125 * 125;
        var bounds = WalkBounds(workArea);
        var activeDisplay = new DesktopRoam.Display(screen.DeviceName, DesktopRect(screen.Bounds), bounds);
        var pointerScreen = Forms.Screen.FromPoint(cursor);
        var pointerArea = DesktopRect(pointerScreen.WorkingArea);
        bool sameScreen = screen.Equals(pointerScreen);
        var crossing = sameScreen ? null : DesktopRoam.FindCrossing(position,
            activeDisplay.Frame, DesktopRect(pointerScreen.Bounds), bounds, WalkBounds(pointerArea));
        bool following = roam.TrackPointer(position, pointer,
            pointerArea.Contains(pointer) && (sameScreen || crossing is not null), elapsed);
        pet.LookAt = near || following ? towardsPointer : default;
        pet.PointerPose = PointerPose.None;
        if (pet.Activity == Play.None && !dragging)
        {
            if (pet.IsMouseOver && elapsed - lastHover > 1.35)
                pet.PointerPose = PointerPose.Paw;
            else if (near && (!pet.IsMouseOver || elapsed - lastHover > 0.24))
                pet.PointerPose = PointerPose.Sniff;
        }
        pet.Walking = false;

        bool canRoam = pet.Activity == Play.None
            && !pet.IsMouseCaptured && !dragging && !pet.IsMouseOver && !playTray.IsMouseOver;
        pet.RoamPose = roamTricks.Update(elapsed, canRoam && !following);
        if (!canRoam)
        {
            // Keep the hit area stable under the cursor during an interaction.
            if (!pet.IsMouseCaptured && !dragging && !pet.IsMouseOver && !playTray.IsMouseOver)
                ResizePet(DesktopRoam.ScaleFor(position, position, icons), frame);
            return;
        }

        var target = roam.Step(position, bounds, elapsed, frame, following ? pointer : null, icons,
            crossing, activeDisplay, displays);
        ResizePet(DesktopRoam.ScaleFor(position, target, icons), frame);
        Vector movement = target - position;
        if (movement.LengthSquared < 0.01) return;
        Left += movement.X;
        Top += movement.Y;
        pet.Walking = true;
        if (Math.Abs(movement.X) > 0.25) pet.FacingLeft = movement.X < 0;
    }

    private void ResizePet(double target, double frame)
    {
        double change = target - petScale.ScaleX;
        if (Math.Abs(change) < 0.001) return;
        double scale = petScale.ScaleX + Math.Clamp(change, -3.5 * frame, 2.2 * frame);
        petScale.ScaleX = petScale.ScaleY = scale;
        Canvas.SetTop(playTray, 263 - (1 - scale) * 39);
    }

    private void CheckRub(double mouseX)
    {
        double delta = mouseX - strokeX;
        if (Math.Abs(delta) < 13) return;
        strokeX = mouseX;
        double elapsed = clock.Elapsed.TotalSeconds;
        if (elapsed - lastStroke > 0.6) strokes = 0;
        lastStroke = elapsed;
        int direction = Math.Sign(delta);
        if (strokeDirection != direction) strokes++;
        strokeDirection = direction;
        if (strokes < 3 || pet.Activity != Play.None) return;
        strokes = 0;
        StartPlay(Play.Rub);
    }

    private void RevealPlay()
    {
        playTray.Visibility = Visibility.Visible;
        OfferPlayBriefly();
    }

    private void OfferPlayBriefly() => offerPlayUntil = pet.IsMouseOver || playTray.IsMouseOver
        ? double.PositiveInfinity
        : clock.Elapsed.TotalSeconds + 3;

    internal void StartPlay(Play activity)
    {
        ReleaseHighFivePointer();
        pet.Activity = activity == Play.Nap && pet.Activity == Play.Nap ? Play.Wake : activity;
        pet.PlayTime = 0;
        playingSince = clock.Elapsed.TotalSeconds;
        napButton.Content = pet.Activity == Play.Nap ? "叫醒" : "小睡";
        RevealPlay();
        pet.InvalidateVisual();
    }

    public void StartDemo()
    {
        foreach (var key in sessions.Keys.Where(key => key.Item3).ToArray()) sessions.Remove(key);
        foreach (var window in demoWindows.Values.ToArray()) window.Close();
        SetQuiet(false);
        demoStage = 0;
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 26;
        Top = work.Bottom - Height - 13;
        firstDemo.Stop();
        firstDemo.Start();
    }

    private void Dismiss()
    {
        if (current is not { } item) return;
        sessions.Remove((item.Provider, item.Session, item.Demo));
        selection = 0;
        if (item.Demo) DemoTo(demoStage + 1);
        else Refresh();
    }

    private void DemoTo(int stage)
    {
        demoStage = stage;
        foreach (var key in sessions.Keys.Where(key => key.Item3).ToArray()) sessions.Remove(key);
        switch (stage)
        {
            case 1: Receive(Notice.Sample("Claude Code", Mood.Approval)); break;
            case 2: Receive(Notice.Sample("Codex CLI", Mood.Question)); break;
            case 3: Receive(Notice.Sample("Codex CLI", Mood.Done)); break;
            default: Refresh(); break;
        }
    }

    private void Jump()
    {
        if (current is not { } item) return;
        if (item.Provider == T3Completion.Provider)
        {
            if (!T3Completion.OpenThread(item.Link)) detail.Text = "无法打开 T3 Code 会话链接";
            return;
        }
        if (!item.Demo)
        {
            if (WindowsNative.BringForward(item)) detail.Text = "已切到任务窗口";
            else detail.Text = "窗口已关闭或 Windows 未能切换，请手动打开任务";
            return;
        }

        if (demoWindows.TryGetValue(item.Provider, out var old)) old.Close();
        var target = new Window
        {
            Title = $"巡巡演示 · {item.Provider}", Width = 480, Height = 305,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = ColorBrush("#23233C"), FontFamily = FontFamily
        };
        var layout = new StackPanel { Margin = new Thickness(28, 24, 28, 16) };
        layout.Children.Add(Label(item.Provider + "  ·  模拟任务窗口", 13, "#8FF2D0", true));
        var title = Label(headline.Text, 26, "#FEF8EC", true);
        title.Margin = new Thickness(0, 14, 0, 6);
        layout.Children.Add(title);
        layout.Children.Add(Label(detail.Text, 14, "#CECEE1"));
        var explain = Label("你已从巡巡的提醒切换到这个演示窗口。", 13, "#CECEE1");
        explain.Margin = new Thickness(0, 14, 0, 17);
        layout.Children.Add(explain);
        var action = Action(item.Mood == Mood.Done ? "结束演示" : "下一段演示  →", true);
        action.HorizontalAlignment = HorizontalAlignment.Left;
        action.Click += (_, _) =>
        {
            target.Close();
            if (current == item) DemoTo(demoStage + 1);
        };
        layout.Children.Add(action);
        target.Content = layout;
        demoWindows[item.Provider] = target;
        target.Closed += (_, _) => demoWindows.Remove(item.Provider);
        target.Show();
        target.Activate();
        detail.Text = "已打开演示任务 · 在任务窗口继续下一段";
    }

    public IntPtr DemoHandle(string provider) => demoWindows.TryGetValue(provider, out var window)
        ? new WindowInteropHelper(window).Handle : IntPtr.Zero;

    public void PreviewPose(double time, double playTime = 0, PointerPose pointer = PointerPose.None,
        RoamPose performance = default, bool grabbed = false)
    {
        pet.Time = time;
        pet.PlayTime = playTime;
        pet.PointerPose = pointer;
        pet.RoamPose = performance;
        pet.IsBeingDragged = grabbed;
        pet.LookAt = pointer == PointerPose.None ? default : new Vector(56, -22);
        pet.InvalidateVisual();
    }

    private static void AddMenu(ItemsControl menu, string title, System.Action callback)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => callback();
        menu.Items.Add(item);
    }

    internal static SolidColorBrush ColorBrush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    internal static TextBlock Label(string text, double size, string color, bool bold = false) =>
        new() { Text = text, FontSize = size, Foreground = ColorBrush(color),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    internal static Button Action(string title, bool primary)
    {
        var button = new Button
        {
            Content = title, Foreground = ColorBrush(primary ? "#1C302F" : "#DEDCF4"),
            Background = ColorBrush(primary ? "#8FF2D0" : "#373750"),
            Padding = primary ? new Thickness(12, 6, 12, 6) : new Thickness(8, 4, 8, 4),
            FontSize = 12, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand
        };
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        frame.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        foreach (var (property, opacity) in new[] { (IsMouseOverProperty, 0.86), (Button.IsPressedProperty, 0.7) })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(OpacityProperty, opacity));
            template.Triggers.Add(trigger);
        }
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.5));
        template.Triggers.Add(disabled);
        button.Template = template;
        return button;
    }
}
