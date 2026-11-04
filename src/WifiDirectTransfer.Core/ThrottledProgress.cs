using System.Diagnostics;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 限制进度回调频率。首次和最终值始终上报，避免 UI 被每个 64 KiB 块淹没。
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly object _gate = new();
    private readonly Action<T> _report;
    private readonly TimeSpan _minimumInterval;
    private readonly Func<T, bool>? _isFinal;
    private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();

    private bool _hasReported;

    public ThrottledProgress(
        Action<T> report,
        TimeSpan minimumInterval,
        Func<T, bool>? isFinal = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumInterval),
                "进度间隔不能为负数。");
        }

        _report = report;
        _minimumInterval = minimumInterval;
        _isFinal = isFinal;
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            var isFinal = _isFinal?.Invoke(value) == true;

            if (!_hasReported ||
                isFinal ||
                _sinceLastReport.Elapsed >= _minimumInterval)
            {
                _hasReported = true;
                _sinceLastReport.Restart();
                _report(value);
            }
        }
    }
}
