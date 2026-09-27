using System.Windows;
using LibVLCSharp.Shared;

namespace SpikeWpf;

public partial class HoverWindow : Window
{
    public HoverWindow()
    {
        InitializeComponent();
    }

    public LibVLCSharp.WPF.VideoView Video => HoverVideo;

    public void SetLabel(string text) => HoverLabel.Text = text;

    public void SetTime(long ms, long totalMs)
        => HoverTime.Text = $"{Fmt(ms)} / {Fmt(totalMs)}";

    private static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    /// <summary>ShowActivated=False 时 WPF 仍可能抢焦点，这里显式验证并报告。</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        SpikeLog.Write("HoverWindow.OnActivated 被触发（注意：若出现说明抢焦点了）");
    }
}
