using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace XunXun;

static class BriefingSettingsWindow
{
    public static BriefingOptions? Edit(Window owner, BriefingOptions options, BriefingClient client,
        CancellationToken token)
    {
        using var search = CancellationTokenSource.CreateLinkedTokenSource(token);
        var body = new StackPanel();
        var (automatic, time) = ScheduleRow(body, "每天推送简报", options.Enabled, options.Time);
        var (recap, recapTime) = ScheduleRow(body, "今日任务小结", options.Recap, options.RecapTime);
        var weather = AddCheck(body, "出门天气", options.Weather);
        var cityRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        var lookup = PetWindow.Action("搜索城市", false);
        lookup.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(lookup, Dock.Right);
        cityRow.Children.Add(lookup);
        var query = Input("城市名称", "", 75);
        cityRow.Children.Add(query);
        body.Children.Add(cityRow);
        var choices = new ComboBox { Padding = new Thickness(5, 3, 5, 3), DisplayMemberPath = nameof(BriefingCity.Label),
            Margin = new Thickness(0, 0, 0, 9) };
        AutomationProperties.SetName(choices, "已选城市");
        if (options.City is { } city)
        {
            choices.ItemsSource = new[] { city };
            choices.SelectedIndex = 0;
        }
        else choices.Visibility = Visibility.Collapsed;
        body.Children.Add(choices);
        var cityStatus = PetWindow.Label("", 11, "#CBC9E0");
        cityStatus.Visibility = Visibility.Collapsed;
        cityStatus.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(cityStatus);
        var releases = AddCheck(body, "Claude Code 和 Codex 正式版", options.Releases);
        var github = AddCheck(body, "GitHub 主题 Top 3", options.Github);
        var topic = AddInput(body, "GitHub 主题", options.Topic, 35);
        var news = AddCheck(body, "前沿 AI 资讯", options.News);
        var keywords = AddInput(body, "资讯关键词（用逗号分隔，可留空）", options.Keywords, 160);
        var error = PetWindow.Label("", 12, "#FFC185");
        error.TextWrapping = TextWrapping.Wrap;
        error.Visibility = Visibility.Collapsed;
        body.Children.Add(error);

        var scroll = new StackPanel();
        scroll.Children.Add(new ScrollViewer { Content = body, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var save = PetWindow.Action("保存", true);
        save.IsDefault = true;
        var cancel = PetWindow.Action("取消", false);
        cancel.IsCancel = true;
        var dialog = HookSetupWindow.Create(owner, "巡巡 · 订阅设置", "订阅设置", "定时推送只补发当天，并在本机保存简报。", scroll, save, cancel);
        dialog.Closed += (_, _) => search.Cancel();
        BriefingOptions? result = null;

        lookup.Click += async (_, _) =>
        {
            string term = query.Text.Trim();
            cityStatus.Visibility = Visibility.Visible;
            if (term.Length < 2)
            {
                cityStatus.Text = "请输入至少两个字符。";
                return;
            }
            lookup.IsEnabled = false;
            cityStatus.Text = "正在搜索…";
            try
            {
                var results = await client.FindCities(term, search.Token);
                if (search.IsCancellationRequested) return;
                if (results.Length == 0)
                {
                    cityStatus.Text = "没有找到匹配城市。";
                    return;
                }
                choices.ItemsSource = results;
                choices.SelectedIndex = -1;
                choices.Visibility = Visibility.Visible;
                choices.IsDropDownOpen = true;
                cityStatus.Text = "从列表中选择城市。";
            }
            catch (Exception failure) when (BriefingClient.IsSourceError(failure))
            {
                if (!search.IsCancellationRequested) cityStatus.Text = BriefingClient.ErrorMessage(failure);
            }
            finally { if (!search.IsCancellationRequested) lookup.IsEnabled = true; }
        };
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is BriefingCity) cityStatus.Visibility = Visibility.Collapsed;
        };
        save.Click += (_, _) =>
        {
            string briefAt = time.Text.Trim();
            string recapAt = recapTime.Text.Trim();
            string slug = topic.Text.Trim().ToLowerInvariant();
            string? failure = null;
            if (!BriefingOptions.ValidTime(briefAt) || !BriefingOptions.ValidTime(recapAt))
                failure = "时间请用 24 小时格式，例如 08:30。";
            else if (github.IsChecked == true && !BriefingOptions.ValidTopic(slug))
                failure = "GitHub 主题只能使用英文小写字母、数字和短横线。";
            else if (weather.IsChecked == true && choices.IsVisible && choices.SelectedItem is null)
                failure = "请选择一个城市。";
            if (failure is not null)
            {
                error.Text = failure;
                error.Visibility = Visibility.Visible;
                return;
            }
            result = new BriefingOptions(automatic.IsChecked == true, briefAt, weather.IsChecked == true,
                choices.SelectedItem as BriefingCity ?? options.City, releases.IsChecked == true, github.IsChecked == true,
                slug, news.IsChecked == true, keywords.Text, recap.IsChecked == true, recapAt).Normalize();
            if (result.Enabled && !result.Sources().Any())
            {
                error.Text = "请启用一个栏目，或为天气选定城市。";
                error.Visibility = Visibility.Visible;
                return;
            }
            dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true) return null;
        return result;
    }

    private static (CheckBox, TextBox) ScheduleRow(Panel parent, string label, bool enabled, string time)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var field = Input($"{label}时间", time, 5);
        field.Width = 65;
        DockPanel.SetDock(field, Dock.Right);
        row.Children.Add(field);
        var check = AddCheck(row, label, enabled);
        parent.Children.Add(row);
        return (check, field);
    }

    private static CheckBox AddCheck(Panel parent, string title, bool enabled)
    {
        var check = new CheckBox { Content = title, IsChecked = enabled, Foreground = PetWindow.ColorBrush("#F1F0FF"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
        parent.Children.Add(check);
        return check;
    }

    private static TextBox AddInput(Panel parent, string title, string value, int limit)
    {
        parent.Children.Add(PetWindow.Label(title, 11, "#CBC9E0"));
        var field = Input(title, value, limit);
        field.Margin = new Thickness(0, 3, 0, 8);
        parent.Children.Add(field);
        return field;
    }

    private static TextBox Input(string title, string value, int limit)
    {
        var field = new TextBox { Text = value, MaxLength = limit, Padding = new Thickness(6, 4, 6, 4) };
        AutomationProperties.SetName(field, title);
        return field;
    }
}
