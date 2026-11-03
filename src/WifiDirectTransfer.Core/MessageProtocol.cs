namespace WifiDirectTransfer.Core;

/// <summary>
/// M1 外层消息帧参数。帧头使用大端，FileHeader payload 仍使用小端。
/// </summary>
public static class MessageProtocol
{
    public const int FrameHeaderLength = 5;

    /// <summary>
    /// 连接建立后由双方交换的协议版本。版本不一致时拒绝传输。
    /// </summary>
    public const int ProtocolVersion = 2;

    /// <summary>默认单帧 payload 上限，防止异常长度导致过大分配。</summary>
    public const int DefaultMaxPayloadLength = 1024 * 1024;

    public const int FileEndDigestLength = 32;

    public static bool IsDefined(MessageType type) =>
        Enum.IsDefined(typeof(MessageType), type);
}
