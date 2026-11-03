namespace WifiDirectTransfer.Core;

/// <summary>
/// FileStart 帧的完整 payload：24 字节 FileHeader + 原始 UTF-8 文件名。
/// 文件名尚未经过 SafeFileName 净化。
/// </summary>
public sealed record FileStartPayload(FileHeader Header, string FileName);
