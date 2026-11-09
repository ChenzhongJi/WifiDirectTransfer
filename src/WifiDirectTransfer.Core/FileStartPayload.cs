namespace WifiDirectTransfer.Core;

/// <summary>
/// FileStart 帧的完整 payload：24 字节 FileHeader + 原始 UTF-8 文件名 + 原始 UTF-8 相对目录。
/// 文件名和相对目录尚未经过路径净化。
/// </summary>
public sealed record FileStartPayload(
    FileHeader Header,
    string FileName,
    string RelativeDirectory);
