using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

internal sealed class UpdateDownloadProgress
{
    public UpdateDownloadProgress(long received, long total)
    {
        Received = received;
        Total = total;
    }

    public long Received { get; }

    public long Total { get; }

    public double Fraction => Total <= 0
        ? 0
        : Math.Clamp((double)Received / Total, 0, 1);
}

/// <summary>
/// 检查、下载并校验更新包。只接受 HTTPS 和 GitHub 域名，
/// 本地测试可通过 WDT_UPDATE_FEED 指到 file:// 或 localhost。
/// </summary>
internal static class UpdateService
{
    /// <summary>必须与 installer\WifiDirectTransfer.iss 中的 AppId 保持一致。</summary>
    public const string InstallerAppId =
        "{8F3C2A1E-5B7D-4E9A-9C41-2D6B8F0A7E13}";

    public const string ReleasesPageUrl =
        "https://github.com/ChenzhongJi/WifiDirectTransfer/releases";

    private const string DefaultFeedUrl =
        "https://github.com/ChenzhongJi/WifiDirectTransfer/" +
        "releases/latest/download/latest.json";

    private const string FeedOverrideVariable = "WDT_UPDATE_FEED";

    private static readonly string[] AllowedDownloadHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    public static ReleaseVersion CurrentVersion { get; } =
        ResolveCurrentVersion();

    public static string CurrentVersionText => CurrentVersion.ToString();

    public static Uri FeedUri { get; } = ResolveFeedUri();

    /// <summary>
    /// 只有从安装器安装、且当前目录就是安装目录时才允许自我更新；
    /// 便携版和开发目录只提示下载页。
    /// </summary>
    public static bool IsInstalledCopy => CheckInstalledCopy();

    public static string UpdateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiDirectTransfer",
        "updates");

    public static async Task<UpdateManifest?> CheckAsync(
        CancellationToken cancellationToken)
    {
        if (!IsAllowedFeedUri(FeedUri))
        {
            throw new InvalidOperationException("更新源地址无效。");
        }

        if (FeedUri.Scheme == Uri.UriSchemeFile)
        {
            var json = await File
                .ReadAllTextAsync(FeedUri.LocalPath, cancellationToken)
                .ConfigureAwait(false);
            return UpdateManifest.Parse(json);
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = CheckTimeout
        };
        using var response = await SendWithRedirectsAsync(
                client,
                FeedUri,
                cancellationToken,
                allowNotFound: true)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var content = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return UpdateManifest.Parse(content);
    }

    public static async Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(UpdateDirectory);
        CleanupPreviousDownloads();

        var fileName =
            $"WifiDirectTransfer-{manifest.Version}-setup.exe";
        var targetPath = Path.Combine(UpdateDirectory, fileName);
        var partPath = targetPath + ".part";

        try
        {
            if (manifest.SetupUri.Scheme == Uri.UriSchemeFile)
            {
                await CopyLocalFileAsync(
                        manifest.SetupUri.LocalPath,
                        partPath,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await DownloadRemoteAsync(
                        manifest.SetupUri,
                        partPath,
                        manifest.Size,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            VerifyDownload(partPath, manifest.Size, manifest.Sha256);
            File.Move(partPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    public static void CleanupPreviousDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdateDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(UpdateDirectory))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch (IOException)
        {
            // 清理失败不影响本次下载。
        }
        catch (UnauthorizedAccessException)
        {
            // 清理失败不影响本次下载。
        }
    }

    private static async Task DownloadRemoteAsync(
        Uri uri,
        string destinationPath,
        long expectedSize,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var response = await SendWithRedirectsAsync(
                client,
                uri,
                cancellationToken)
            .ConfigureAwait(false);
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var lastReported = -1L;

        while (true)
        {
            var read = await source
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            await destination
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            received += read;

            // 每 256 KiB 或每 1% 更新一次，避免 UI 线程过载。
            if (received - lastReported >= 256 * 1024)
            {
                lastReported = received;
                progress?.Report(new UpdateDownloadProgress(received, total));
            }
        }

        progress?.Report(new UpdateDownloadProgress(received, total));
    }

    private static async Task CopyLocalFileAsync(
        string sourcePath,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        var total = source.Length;
        long copied = 0;

        while (true)
        {
            var read = await source
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            await destination
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            copied += read;
            progress?.Report(new UpdateDownloadProgress(copied, total));
        }
    }

    private static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        var current = uri;

        for (var hop = 0; hop < 5; hop++)
        {
            if (!IsAllowedDownloadUri(current))
            {
                throw new InvalidOperationException(
                    "更新地址不在允许的域名范围内。");
            }

            var response = await client
                .GetAsync(
                    current,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;

                if (location is null)
                {
                    response.Dispose();
                    throw new InvalidOperationException(
                        "更新下载返回了无效的重定向。");
                }

                current = location.IsAbsoluteUri
                    ? location
                    : new Uri(current, location);
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                if (allowNotFound &&
                    response.StatusCode == HttpStatusCode.NotFound)
                {
                    return response;
                }

                var status = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    $"更新下载失败（HTTP {status}）。");
            }

            return response;
        }

        throw new InvalidOperationException("更新下载重定向次数过多。");
    }

    private static void VerifyDownload(
        string path,
        long expectedSize,
        string expectedSha256)
    {
        var actualSize = new FileInfo(path).Length;

        if (actualSize != expectedSize)
        {
            throw new InvalidDataException(
                $"更新包大小不匹配：预期 {expectedSize} 字节，实际 {actualSize} 字节。");
        }

        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var actualHash = Convert.ToHexString(sha256.ComputeHash(stream));

        if (!string.Equals(
                actualHash,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新包 SHA-256 校验失败。");
        }
    }

    private static bool CheckInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" +
                InstallerAppId + "_is1");

            if (key?.GetValue("InstallLocation") is not string location ||
                string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            var expected = Path
                .GetFullPath(location)
                .TrimEnd(Path.DirectorySeparatorChar);
            var actual = Path
                .GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar);

            return string.Equals(
                       expected,
                       actual,
                       StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(
                       Path.Combine(expected, "WifiDirectTransfer.App.exe"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ReleaseVersion ResolveCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (ReleaseVersion.TryParse(informational, out var version))
        {
            return version;
        }

        var fileVersion = assembly.GetName().Version;

        return fileVersion is null
            ? new ReleaseVersion(0, 0, 0)
            : new ReleaseVersion(
                fileVersion.Major,
                fileVersion.Minor,
                Math.Max(fileVersion.Build, 0));
    }

    private static Uri ResolveFeedUri()
    {
        var value = Environment.GetEnvironmentVariable(FeedOverrideVariable);

        if (!string.IsNullOrWhiteSpace(value) &&
            Uri.TryCreate(value.Trim(), UriKind.Absolute, out var overridden))
        {
            return overridden;
        }

        return new Uri(DefaultFeedUrl);
    }

    private static bool IsAllowedFeedUri(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeFile)
        {
            return true;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttps &&
               AllowedDownloadHosts.Contains(
                   uri.Host,
                   StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAllowedDownloadUri(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeFile)
        {
            return true;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttps &&
               AllowedDownloadHosts.Contains(
                   uri.Host,
                   StringComparer.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删除失败不影响调用方。
        }
        catch (UnauthorizedAccessException)
        {
            // 删除失败不影响调用方。
        }
    }
}
