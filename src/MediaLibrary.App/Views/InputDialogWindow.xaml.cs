using System.Windows;
using System.Windows.Controls;

namespace MediaLibrary.App.Views;

/// <summary>通用单行输入对话框（新建文件夹命名等）。</summary>
public partial class InputDialogWindow : Window
{
    public InputDialogWindow(string prompt, string title = "输入", string? initial = null)
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;

        if (!string.IsNullOrEmpty(initial))
            InputBox.Text = initial;
    }

    /// <summary>用户输入的内容。DialogResult 为 true 时有效。</summary>
    public string? Input { get; private set; }

    private void OnInputLoaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            ErrorText.Text = "不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            InputBox.Focus();
            return;
        }

        Input = text;
        DialogResult = true;
        Close();
    }
}
