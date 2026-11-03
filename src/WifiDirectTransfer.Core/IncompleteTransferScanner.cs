namespace WifiDirectTransfer.Core;

public sealed record IncompleteTransfer(
    string PartPath,
    string? MetaPath,
    string FileName,
    long? ExpectedBytes,
    long ReceivedBytes,
    DateTimeOffset LastWriteTime);

/// <summary>
/// 启动时扫描接收目录中遗留的 .part / .meta，用于崩溃恢复提示。
/// </summary>
public static class IncompleteTransferScanner
{
    public static IReadOnlyList<IncompleteTransfer> Scan(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<IncompleteTransfer>();
        }

        var results = new List<IncompleteTransfer>();

        foreach (var partPath in Directory.EnumerateFiles(directory, "*.part"))
        {
            try
            {
                var metaPath = partPath[..^".part".Length] + ".meta";
                var metaExists = File.Exists(metaPath);
                TransferMetadata? metadata = null;

                if (metaExists)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(metaPath);

                        if (!TransferMetadataCodec.TryDeserialize(
                                bytes,
                                out metadata))
                        {
                            metadata = null;
                        }
                    }
                    catch (Exception)
                    {
                        metadata = null;
                    }
                }

                var partInfo = new FileInfo(partPath);
                var partLength = partInfo.Length;
                var fileName = metadata?.FileName
                               ?? Path.GetFileNameWithoutExtension(partPath);
                var receivedBytes = metadata is null
                    ? partLength
                    : Math.Min(metadata.PersistedBytes, partLength);

                results.Add(
                    new IncompleteTransfer(
                        partPath,
                        metaExists ? metaPath : null,
                        fileName,
                        metadata?.FileSize,
                        receivedBytes,
                        partInfo.LastWriteTime));
            }
            catch (Exception)
            {
                // 单个文件扫描失败不影响其他条目。
            }
        }

        return results
            .OrderByDescending(item => item.LastWriteTime)
            .ToArray();
    }

    public static void Delete(IEnumerable<IncompleteTransfer> transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);

        foreach (var transfer in transfers)
        {
            TryDelete(transfer.PartPath);
            TryDelete(transfer.MetaPath);
            TryDelete(transfer.MetaPath + ".tmp");
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 文件可能仍被占用，忽略。
        }
        catch (UnauthorizedAccessException)
        {
            // 没有权限时忽略。
        }
    }
}
