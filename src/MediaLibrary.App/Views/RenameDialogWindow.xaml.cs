using System.Windows;
using System.Windows.Controls;

namespace MediaLibrary.App.Views;

/// <summary>批量重命名对话框。返回用户确认后的名称模板。</summary>
public partial class RenameDialogWindow : Window
{
    public sealed record PreviewRow(string Arrow, string Name);

    private readonly string _folderName;
    private readonly List<PreviewRow> _rows = [];

    /// <summary>用户确认后的模板（含占位符）。</summary>
    public string? Pattern { get; private set; }

    public RenameDialogWindow(string folderName)
    {
        _folderName = folderName;

        InitializeComponent();

        PatternBox.Text = $"{folderName}_{{序号:D3}}";
        PreviewList.ItemsSource = _rows;

        RefreshPreview();
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e)
        => RefreshPreview();

    private void RefreshPreview()
    {
        _rows.Clear();
        var pattern = PatternBox.Text ?? "";

        for (var i = 1; i <= 8; i++)
        {
            var name = pattern
                .Replace("{序号:D3}", i.ToString("D3"))
                .Replace("{序号}", i.ToString())
                .Replace("{原名}", "原名")
                .Replace("{原文件夹}", _folderName);

            _rows.Add(new PreviewRow("→", $"{name}.扩展名"));
        }

        PreviewList.Items.Refresh();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PatternBox.Text))
        {
            System.Windows.MessageBox.Show(
                this, "请先输入名称模板。", "素材库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Pattern = PatternBox.Text.Trim();
        DialogResult = true;
    }
}
