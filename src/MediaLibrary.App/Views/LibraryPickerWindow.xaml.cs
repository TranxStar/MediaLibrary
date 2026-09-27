using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MediaLibrary.App.Views;

/// <summary>首次启动 / 设置里更改库位置时用的【纯文件夹选择】窗口：点开即弹系统选择框，无需输入路径。</summary>
public partial class LibraryPickerWindow : Window
{
    private string? _pickedPath;

    public LibraryPickerWindow()
    {
        InitializeComponent();

        // 打开即弹出系统文件夹选择框 —— 用户只需选，不需要打字
        Loaded += (_, _) =>
        {
            if (_pickedPath is null) Browse();
        };
    }

    /// <summary>用户确认的库根目录。DialogResult 为 true 时有效。</summary>
    public string? SelectedPath { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e) => Browse();

    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择素材库文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            // 取消选择 → 窗口保留，用户可再点"选择文件夹…"或点"取消"退出
            return;
        }

        _pickedPath = dialog.FolderName;
        PathText.Text = _pickedPath;
        Validate(_pickedPath);
    }

    private void Validate(string path)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
        ConfirmButton.IsEnabled = false;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowError("该文件夹不存在。");
            return;
        }

        // 复用素材库自身的可写检测
        var layout = new Core.LibraryLayout(path);
        if (!layout.IsWritable())
        {
            ShowError("该文件夹不可写。请换一个位置，或以管理员身份运行。");
            return;
        }

        var free = layout.GetFreeSpaceBytes();
        if (free > 0 && free < 5L * 1024 * 1024 * 1024)
        {
            ShowError($"该磁盘剩余空间只有 {free / 1024.0 / 1024 / 1024:F1} GB，不足以存放素材。请换一个磁盘。");
            return;
        }

        var isExisting = Core.LibraryLayout.LooksLikeLibrary(path);
        var hasContent = Directory.EnumerateFileSystemEntries(path)
            .Any(p => !Path.GetFileName(p).StartsWith('.'));

        HintText.Text = isExisting
            ? "✅ 检测到已存在的素材库，将直接打开。"
            : hasContent
                ? "ℹ 该文件夹已有内容。软件会扫描其中的媒体文件并收编入库，不会删除任何东西。"
                : "✅ 将在此新建素材库。可写，空间充足。";

        ConfirmButton.IsEnabled = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pickedPath)) return;

        SelectedPath = _pickedPath;
        DialogResult = true;
        Close();
    }
}
