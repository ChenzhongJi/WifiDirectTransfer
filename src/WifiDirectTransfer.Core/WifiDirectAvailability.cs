namespace WifiDirectTransfer.Core;

/// <summary>
/// Wi-Fi Direct 能力预检结果。
/// </summary>
public sealed record WifiDirectAvailability(
    bool IsSupported,
    bool IsBrowseSupported,
    string? UnsupportedReason)
{
    public static WifiDirectAvailability Supported() => new(true, true, null);

    public static WifiDirectAvailability Unsupported(string reason, bool isBrowseSupported = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new WifiDirectAvailability(false, isBrowseSupported, reason);
    }
}
