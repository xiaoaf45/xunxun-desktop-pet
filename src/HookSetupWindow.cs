using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace XunXun;

static class HookSetupWindow
{
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
