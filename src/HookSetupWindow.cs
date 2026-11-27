using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace XunXun;

static class HookSetupWindow
{
    public static void Launch(Window owner)
    {
        var tools = HarnessLauncher.AvailableTools();
        if (tools.Length == 0)
        {
            Report(owner, "启动助手", ["未找到已安装的 Claude Code 或 Codex CLI。"]);
            return;
        }

        var body = new StackPanel();
        var launch = PetWindow.Action("启动终端", true);
        launch.IsDefault = true;
        var cancel = PetWindow.Action("取消", false);
        cancel.IsCancel = true;
        var dialog = Create(owner, "巡巡 · 启动助手", "启动助手", null, body, launch, cancel);
        AutomationProperties.SetAutomationId(launch, "launch-terminal");

        var toolLabel = PetWindow.Label("工具", 12, "#D1D0E3", true);
        var provider = new ComboBox { ItemsSource = tools, DisplayMemberPath = nameof(HarnessLauncher.Tool.Name),
            SelectedIndex = 0, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 5, 0, 13) };
        AutomationProperties.SetLabeledBy(provider, toolLabel);
        AutomationProperties.SetAutomationId(provider, "launch-tool");
        body.Children.Add(toolLabel);
        body.Children.Add(provider);

        var history = new ProjectHistory();
        var paths = history.Read();
        var pathLabel = PetWindow.Label("项目路径", 12, "#D1D0E3", true);
        var path = new ComboBox
        {
            IsEditable = true, IsTextSearchEnabled = false, ItemsSource = paths, Text = paths.FirstOrDefault() ?? "",
            Padding = new Thickness(7, 5, 7, 5), MaxDropDownHeight = 230, MinWidth = 250
        };
        var browse = PetWindow.Action("浏览…", false);
        browse.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetLabeledBy(path, pathLabel);
        AutomationProperties.SetAutomationId(path, "launch-project");
        var pathRow = new DockPanel { Margin = new Thickness(0, 5, 0, 13) };
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(path);
        body.Children.Add(pathLabel);
        body.Children.Add(pathRow);

        var permission = new CheckBox
        {
            IsChecked = false, Content = "无需审批（仅本次）", Foreground = PetWindow.ColorBrush("#F1F0FF"),
            ToolTip = "关闭时沿用该工具的现有审批设置。"
        };
        AutomationProperties.SetAutomationId(permission, "launch-no-approval");
        body.Children.Add(permission);
        var mode = Wrapped("", 11, "#D1D0E3", new Thickness(20, 5, 0, 0));
        var error = Wrapped("", 12, "#FFC185", new Thickness(0, 9, 0, 0));
        mode.Visibility = error.Visibility = Visibility.Collapsed;
        body.Children.Add(mode);
        body.Children.Add(error);

        void UpdateMode()
        {
            mode.Text = provider.SelectedItem is HarnessLauncher.Tool { Name: "Codex CLI" }
                ? "跳过 Codex 命令审批，沿用当前沙箱权限。"
                : "Claude Code 将跳过工具权限审批。";
            mode.Visibility = permission.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        permission.Checked += (_, _) => UpdateMode();
        permission.Unchecked += (_, _) => UpdateMode();
        provider.SelectionChanged += (_, _) => UpdateMode();

        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择项目目录" };
            if (HarnessLauncher.ExistingDirectory(path.Text) is { } current) picker.InitialDirectory = current;
            if (picker.ShowDialog(dialog) == true) path.Text = picker.FolderName;
        };
        launch.Click += (_, _) =>
        {
            if (HarnessLauncher.ExistingDirectory(path.Text) is not { } directory)
            {
                error.Text = "请选择一个存在的项目目录。";
                error.Visibility = Visibility.Visible;
                path.Focus();
                return;
            }
            if (provider.SelectedItem is not HarnessLauncher.Tool tool) return;
            try
            {
                HarnessLauncher.Start(tool, directory, permission.IsChecked == true);
                history.Remember(directory);
                dialog.DialogResult = true;
            }
            catch (Win32Exception failure)
            {
                error.Text = "无法启动终端。\n" + failure.Message;
                error.Visibility = Visibility.Visible;
            }
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        dialog.ShowDialog();
    }

    public static HookRegistration.Target[] Ask(Window owner, List<HookRegistration.Target> targets, string profile)
    {
        var body = new StackPanel();
        var accept = PetWindow.Action("注册所选 Hook", true);
        var later = PetWindow.Action("暂不接入", false);
        later.ToolTip = "可以从巡巡的右键菜单再次接入。";
        AutomationProperties.SetAutomationId(accept, "hook-accept");
        AutomationProperties.SetAutomationId(later, "hook-later");
        later.IsCancel = true;

        var choices = new List<(HookRegistration.Target Target, CheckBox Check)>();
        var dialog = Create(owner, "巡巡 · 注册任务提醒", "接入任务提醒",
            "提问、待审批或本轮结束时，巡巡会提醒你。", body, accept, later);

        foreach (var target in targets)
        {
            var labels = new StackPanel { Margin = new Thickness(6, 0, 0, 0), MaxWidth = 342 };
            labels.Children.Add(PetWindow.Label(target.Name, 14, "#F1F0FF", true));
            var path = PetWindow.Label(ShortPath(profile, target.File), 11, "#B8B8D1");
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.Margin = new Thickness(0, 3, 0, 0);
            path.ToolTip = target.File;
            labels.Children.Add(path);
            var check = new CheckBox { IsChecked = true, Content = labels, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(check, target.Name);
            choices.Add((target, check));
            check.Checked += (_, _) => accept.IsEnabled = true;
            check.Unchecked += (_, _) => accept.IsEnabled = choices.Any(choice => choice.Check.IsChecked == true);
            body.Children.Add(Tile(check));
        }
        body.Children.Add(Wrapped("确认后会把接收程序保存到当前用户目录。", 11, "#ABAACB", new Thickness(0, 4, 0, 0)));
        if (targets.Any(target => target.Cli == "codex"))
            body.Children.Add(Wrapped("Codex 注册后需在 /hooks 中审查并信任。", 11, "#ABAACB", new Thickness(0, 4, 0, 0)));

        accept.Click += (_, _) => dialog.DialogResult = true;
        later.Click += (_, _) => dialog.DialogResult = false;
        return dialog.ShowDialog() == true
            ? choices.Where(choice => choice.Check.IsChecked == true).Select(choice => choice.Target).ToArray()
            : [];
    }

    public static void Report(Window owner, string heading, List<string> messages)
    {
        var body = new StackPanel();
        foreach (var message in messages)
            body.Children.Add(Tile(Wrapped(message, 13, "#E7E6F5", new Thickness(0))));
        var done = PetWindow.Action("完成", true);
        AutomationProperties.SetAutomationId(done, "hook-done");
        done.IsCancel = true;
        done.IsDefault = true;
        var dialog = Create(owner, "巡巡 · " + heading, heading, null, body, done);
        done.Click += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private static Window Create(Window owner, string title, string heading, string? description,
        StackPanel body, Button primary, Button? secondary = null)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Icon = owner.Icon,
            Width = 452,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = owner.Topmost,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            UseLayoutRounding = true
        };

        var layout = new StackPanel();
        var header = new DockPanel();
        var icon = new Image { Source = owner.Icon, Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0) };
        header.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(PetWindow.Label(heading, 20, "#FFFAEE", true));
        if (description is not null)
            titles.Children.Add(Wrapped(description, 12, "#D1D0E3", new Thickness(0, 4, 0, 0)));
        header.Children.Add(titles);
        header.MouseLeftButtonDown += (_, _) => dialog.DragMove();
        layout.Children.Add(header);

        body.Margin = new Thickness(0, 18, 0, 0);
        layout.Children.Add(body);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0) };
        if (secondary is not null)
        {
            secondary.Margin = new Thickness(0, 0, 8, 0);
            secondary.Padding = new Thickness(14, 7, 14, 7);
            actions.Children.Add(secondary);
        }
        primary.Padding = new Thickness(14, 7, 14, 7);
        actions.Children.Add(primary);
        layout.Children.Add(actions);
        dialog.Content = new Border
        {
            Background = PetWindow.ColorBrush("#23233C"),
            BorderBrush = PetWindow.ColorBrush("#5B5775"),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(21, 20, 21, 18),
            Child = layout
        };
        return dialog;
    }

    private static Border Tile(UIElement child) => new()
    {
        Background = PetWindow.ColorBrush("#302E49"),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(11, 9, 11, 9),
        Margin = new Thickness(0, 0, 0, 7),
        Child = child
    };

    private static TextBlock Wrapped(string text, double size, string color, Thickness margin)
    {
        var label = PetWindow.Label(text, size, color);
        label.TextWrapping = TextWrapping.Wrap;
        label.Margin = margin;
        return label;
    }

    private static string ShortPath(string profile, string file) =>
        file.StartsWith(profile.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ? @"~\" + Path.GetRelativePath(profile, file) : file;
}
