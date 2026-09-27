using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaLibrary.App.Services;
using MediaLibrary.Core;

namespace MediaLibrary.App.Views;

/// <summary>
/// 设置窗口：存储位置（库根切换）、右键菜单项、悬浮预览、磁盘预警阈值。
/// 修改即时保存到 settings.json；切换库需要重启应用。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private string? _pendingRoot;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        InitializeComponent();

        RootPathText.Text = settings.LibraryRoot ?? "(未设置)";

        MenuOpenBox.IsChecked = settings.MenuOpen;
        MenuRevealBox.IsChecked = settings.MenuReveal;
        MenuRetranscodeBox.IsChecked = settings.MenuRetranscode;
        MenuRenameBox.IsChecked = settings.MenuRename;
        MenuDeleteBox.IsChecked = settings.MenuDelete;
        MenuCopyPathBox.IsChecked = settings.MenuCopyPath;

        HoverEnabledBox.IsChecked = settings.HoverPreviewEnabled;
        HoverDelaySlider.Value = Math.Clamp(settings.HoverPreviewDelayMs, 100, 800);
        SpaceWarningSlider.Value = Math.Clamp(settings.SpaceWarningGb, 1, 50);

        RefreshLabels();
        RefreshRecentLibraries();
    }

    private void RefreshLabels()
    {
        // 滑块 ValueChanged 会在 XAML 解析阶段触发（此时后面的标签控件还没创建），
        // 必须逐个判空 —— 之前这里抛 NullReferenceException 导致设置窗口打不开
        if (HoverDelayLabel is not null)
            HoverDelayLabel.Text = $"{HoverDelaySlider.Value:F0} ms";

        if (SpaceWarningLabel is not null)
            SpaceWarningLabel.Text = $"{SpaceWarningSlider.Value:F0}";
    }

    private void RefreshRecentLibraries()
    {
        RecentLibrariesList.Items.Clear();
        var roots = _settings.RecentLibraries
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => !string.Equals(p, _settings.LibraryRoot, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        RecentLibrariesHeader.Visibility =
            roots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var root in roots)
        {
            var button = new Button
            {
                Content = $"↻  {root}",
                Tag = root,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
            };
            button.Click += OnRecentLibraryClick;
            RecentLibrariesList.Items.Add(button);
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        _settings.MenuOpen = MenuOpenBox.IsChecked == true;
        _settings.MenuReveal = MenuRevealBox.IsChecked == true;
        _settings.MenuRetranscode = MenuRetranscodeBox.IsChecked == true;
        _settings.MenuRename = MenuRenameBox.IsChecked == true;
        _settings.MenuDelete = MenuDeleteBox.IsChecked == true;
        _settings.MenuCopyPath = MenuCopyPathBox.IsChecked == true;
        _settings.HoverPreviewEnabled = HoverEnabledBox.IsChecked == true;
        _settings.Save();
    }

    private void OnHoverDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings is null || HoverDelaySlider is null) return;

        RefreshLabels();
        _settings.HoverPreviewDelayMs = (int)HoverDelaySlider.Value;
        _settings.Save();
    }

    private void OnSpaceWarningChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings is null || SpaceWarningSlider is null) return;

        RefreshLabels();
        _settings.SpaceWarningGb = (int)SpaceWarningSlider.Value;
        _settings.Save();
    }

    private void OnChangeRoot(object sender, RoutedEventArgs e)
    {
        var picker = new LibraryPickerWindow { Owner = this };
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;

        ApplyRootChange(picker.SelectedPath);
    }

    private void OnRecentLibraryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string root) return;
        ApplyRootChange(root);
    }

    private void OnOpenCurrentRoot(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _settings.LibraryRoot;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                MessageBox.Show(this, "当前存储位置不存在。", "素材库", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开失败：{ex.Message}", "素材库", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ApplyRootChange(string newRoot)
    {
        if (string.Equals(newRoot, _settings.LibraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            StatusHint("已经在这个位置了。");
            return;
        }

        _pendingRoot = newRoot;

        var choice = MessageBox.Show(
            this,
            $"把素材库存储位置改为：\n\n{newRoot}\n\n" +
            "更改后需要重启应用才能生效。\n\n现在重启吗？（未保存的改动不会丢失，设置已自动保存）",
            "素材库 — 更改存储位置",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (choice != MessageBoxResult.Yes) return;

        _settings.RememberLibrary(newRoot);
        _settings.Save();

        RestartApplication();
    }

    private void StatusHint(string message)
        => MessageBox.Show(this, message, "素材库", MessageBoxButton.OK, MessageBoxImage.Information);

    private static void RestartApplication()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 起不来就让用户手动打开
        }

        Application.Current.Shutdown();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
