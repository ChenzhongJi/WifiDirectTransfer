namespace WifiDirectTransfer.Core;

/// <summary>
/// 连续块 ACK 等待时间监控。只产生警告，不改变发送速率。
/// </summary>
public sealed class AckCongestionMonitor
{
    public const string WarningMessage = "链路可能拥塞，传输速度可能下降";

    private readonly TimeSpan _slowAckThreshold;
    private int _consecutiveSlowAcks;
    private bool _warningRaised;

    public AckCongestionMonitor(TimeSpan? slowAckThreshold = null)
    {
        _slowAckThreshold = slowAckThreshold ?? TimeSpan.FromSeconds(10);

        if (_slowAckThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slowAckThreshold),
                "ACK 慢等待阈值必须大于零。");
        }
    }

    /// <summary>
    /// 记录一次 ACK 等待。连续两个块超过阈值时返回 true，且同一慢速区间只提醒一次。
    /// </summary>
    public bool Report(TimeSpan ackWait)
    {
        if (ackWait <= _slowAckThreshold)
        {
            _consecutiveSlowAcks = 0;
            _warningRaised = false;
            return false;
        }

        _consecutiveSlowAcks++;

        if (_consecutiveSlowAcks < 2 || _warningRaised)
        {
            return false;
        }

        _warningRaised = true;
        return true;
    }
}
