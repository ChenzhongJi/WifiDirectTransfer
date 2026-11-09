using System.Diagnostics;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 根据累计字节数和经过时间估算传输速率与剩余时间。
/// 使用指数滑动平均，避免单个块抖动导致界面数字频繁跳动。
/// </summary>
public sealed class TransferRateTracker
{
    private const double SmoothingFactor = 0.3;

    private static readonly TimeSpan DefaultMinimumSampleInterval =
        TimeSpan.FromMilliseconds(200);

    private readonly TimeSpan _minimumSampleInterval;

    private long _lastBytes;
    private TimeSpan _lastElapsed;
    private double _bytesPerSecond;
    private bool _hasValue;

    public TransferRateTracker(TimeSpan? minimumSampleInterval = null)
    {
        _minimumSampleInterval =
            minimumSampleInterval ?? DefaultMinimumSampleInterval;

        if (_minimumSampleInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumSampleInterval),
                "采样间隔不能为负数。");
        }
    }

    /// <summary>是否已经产生过有效速率。</summary>
    public bool HasValue => _hasValue;

    /// <summary>当前平滑后的速率，单位字节/秒。</summary>
    public double BytesPerSecond => _hasValue ? _bytesPerSecond : 0d;

    /// <summary>
    /// 重置到指定累计字节数。开始新文件或重新连接时调用。
    /// </summary>
    public void Reset(long bytesTransferred = 0)
    {
        _lastBytes = Math.Max(0, bytesTransferred);
        _lastElapsed = TimeSpan.Zero;
        _bytesPerSecond = 0d;
        _hasValue = false;
    }

    /// <summary>
    /// 更新累计字节数。elapsed 是从本次传输开始计时的时间。
    /// 采样间隔不足或数据没有前进时保持上一次速率。
    /// </summary>
    public void Update(long bytesTransferred, TimeSpan elapsed)
    {
        if (bytesTransferred < 0)
        {
            bytesTransferred = 0;
        }

        if (elapsed < _lastElapsed)
        {
            Reset(bytesTransferred);
            return;
        }

        var deltaBytes = bytesTransferred - _lastBytes;
        var deltaTime = elapsed - _lastElapsed;

        if (deltaBytes < 0)
        {
            Reset(bytesTransferred);
            return;
        }

        if (deltaTime < _minimumSampleInterval ||
            deltaTime <= TimeSpan.Zero)
        {
            return;
        }

        var instantaneous = deltaBytes / deltaTime.TotalSeconds;

        _bytesPerSecond = _hasValue
            ? (_bytesPerSecond * (1d - SmoothingFactor)) +
              (instantaneous * SmoothingFactor)
            : instantaneous;
        _hasValue = true;
        _lastBytes = bytesTransferred;
        _lastElapsed = elapsed;
    }

    /// <summary>
    /// 估算剩余时间。尚未产生有效速率或总量无效时返回 null。
    /// </summary>
    public TimeSpan? GetRemaining(long totalBytes)
    {
        if (!_hasValue || _bytesPerSecond <= 0d || totalBytes <= 0)
        {
            return null;
        }

        var remainingBytes = totalBytes - _lastBytes;

        if (remainingBytes <= 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = remainingBytes / _bytesPerSecond;

        if (double.IsNaN(seconds) ||
            double.IsInfinity(seconds) ||
            seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>传输速率和剩余时间的统一显示格式。</summary>
public static class TransferFormat
{
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0d ||
            double.IsNaN(bytesPerSecond) ||
            double.IsInfinity(bytesPerSecond))
        {
            return "—";
        }

        if (bytesPerSecond < 1024d)
        {
            return $"{bytesPerSecond:0} B/s";
        }

        if (bytesPerSecond < 1024d * 1024d)
        {
            return $"{bytesPerSecond / 1024d:0.0} KB/s";
        }

        if (bytesPerSecond < 1024d * 1024d * 1024d)
        {
            return $"{bytesPerSecond / (1024d * 1024d):0.0} MB/s";
        }

        return $"{bytesPerSecond / (1024d * 1024d * 1024d):0.00} GB/s";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            return "—";
        }

        if (duration.TotalSeconds < 1d)
        {
            return "不到 1 秒";
        }

        if (duration.TotalHours >= 1d)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
