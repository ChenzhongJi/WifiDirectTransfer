namespace WifiDirectTransfer.Core;

/// <summary>
/// 一个待发送的源文件及其在接收端的相对目录。
/// RelativeDirectory 为空表示发送到接收目录根。
/// </summary>
public sealed record TransferSource(
    string SourcePath,
    string FileName,
    string RelativeDirectory)
{
    public string DisplayPath => string.IsNullOrEmpty(RelativeDirectory)
        ? FileName
        : Path.Combine(RelativeDirectory, FileName);
}
