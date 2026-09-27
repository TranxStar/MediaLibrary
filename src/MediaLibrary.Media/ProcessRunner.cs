using System.Diagnostics;
using System.Text;

namespace MediaLibrary.Media;

/// <summary>外部进程执行结果。</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    /// <summary>合并输出的最后若干行，用于错误提示。</summary>
    public string Tail(int lines = 6)
    {
        var all = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        var split = all.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', split.TakeLast(lines));
    }
}

/// <summary>ffmpeg / ffprobe 等控制台程序的执行封装。</summary>
public static class ProcessRunner
{
    /// <summary>等待输出流真正结束的时间上限（进程退出后仍可能有余量未读完）。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public static Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken ct = default)
        => RunStreamingAsync(executable, arguments, onStandardOutputLine: null, ct);

    /// <summary>
    /// 执行并把 stdout 逐行回调出去。
    ///
    /// ffmpeg 的 <c>-progress pipe:1</c> 会往 stdout 持续输出 <c>key=value</c> 行，
    /// 靠这个回调就能做实时进度（见 <see cref="FfmpegProgress"/>）。
    /// </summary>
    public static async Task<ProcessResult> RunStreamingAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onStandardOutputLine,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult();
                return;
            }

            stdout.AppendLine(e.Data);

            // 进度回调里绝不能抛异常，否则会打断读取线程
            if (onStandardOutputLine is not null)
            {
                try { onStandardOutputLine(e.Data); }
                catch (Exception) { /* 忽略 */ }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrDone.TrySetResult();
            else stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"无法启动 {Path.GetFileName(executable)}：{ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task)
                .WaitAsync(DrainTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (TimeoutException)
        {
            // 进程已退出但输出流没收干净，用已收集到的内容继续
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程可能已退出
        }
    }
}

/// <summary>
/// 解析 ffmpeg <c>-progress pipe:1</c> 的输出。
///
/// 输出形如：
/// <code>
/// frame=120
/// fps=25.0
/// out_time=00:00:04.800000
/// progress=continue
/// </code>
/// </summary>
public sealed class FfmpegProgress
{
    private readonly long _totalMs;
    private readonly IProgress<double>? _progress;
    private readonly Action<long>? _onTimeMs;

    private int _lastReportedPercent = -1;

    /// <param name="totalMs">媒体总时长（毫秒）。≤0 时无法计算比例，只会回报当前时间。</param>
    /// <param name="progress">比例回调，0.0 ~ 1.0。</param>
    /// <param name="onTimeMs">绝对时间回调（毫秒），用于显示"已处理 3:20 / 10:00"。</param>
    public FfmpegProgress(long totalMs, IProgress<double>? progress, Action<long>? onTimeMs = null)
    {
        _totalMs = totalMs;
        _progress = progress;
        _onTimeMs = onTimeMs;
    }

    public long CurrentMs { get; private set; }

    /// <summary>把 ffmpeg 的一行输出喂进来。非进度行会被忽略。</summary>
    public void Feed(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        var separator = line.IndexOf('=');
        if (separator <= 0) return;

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "out_time":
                if (!TryParseTimestamp(value, out var ms)) return;

                CurrentMs = ms;
                _onTimeMs?.Invoke(ms);

                if (_totalMs <= 0 || _progress is null) return;

                var fraction = Math.Clamp((double)ms / _totalMs, 0, 1);

                // 节流：只有整数百分比变化时才上报，避免每秒上百次回调
                var percent = (int)(fraction * 100);
                if (percent == _lastReportedPercent) return;

                _lastReportedPercent = percent;
                _progress.Report(fraction);
                break;

            case "progress" when value == "end":
                _progress?.Report(1.0);
                break;
        }
    }

    /// <summary>解析 <c>HH:MM:SS.ffffff</c> 或 <c>HH:MM:SS</c>。</summary>
    internal static bool TryParseTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split(':');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out var hours)) return false;
        if (!int.TryParse(parts[1], out var minutes)) return false;

        // 秒可能带小数
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return false;

        milliseconds = (long)((hours * 3600 + minutes * 60 + seconds) * 1000);
        return true;
    }
}
