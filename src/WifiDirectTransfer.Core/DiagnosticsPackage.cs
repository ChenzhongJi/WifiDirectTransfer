using System.IO.Compression;
using System.Text;

namespace WifiDirectTransfer.Core;

/// <summary>
/// 把日志和相关文件打包成诊断 zip。文件被占用时以共享读方式读取。
/// </summary>
public static class DiagnosticsPackage
{
    public static void Create(
        string zipPath,
        string infoText,
        IEnumerable<string> filePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(infoText);
        ArgumentNullException.ThrowIfNull(filePaths);

        var fullPath = Path.GetFullPath(zipPath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        using var archive = ZipFile.Open(fullPath, ZipArchiveMode.Create);

        var infoEntry = archive.CreateEntry(
            "info.txt",
            CompressionLevel.Optimal);

        using (var writer = new StreamWriter(
                   infoEntry.Open(),
                   new UTF8Encoding(false)))
        {
            writer.Write(infoText);
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "info.txt"
        };

        foreach (var filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                continue;
            }

            var baseName = Path.GetFileName(filePath);
            var entryName = baseName;
            var suffix = 1;

            while (!usedNames.Add(entryName))
            {
                entryName =
                    Path.GetFileNameWithoutExtension(baseName) +
                    $"-{suffix++}" +
                    Path.GetExtension(baseName);
            }

            var entry = archive.CreateEntry(
                entryName,
                CompressionLevel.Optimal);

            using var source = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }
}
