using WifiDirectTransfer.Core;
using Windows.Networking.Proximity;

namespace WifiDirectTransfer.Windows;

/// <summary>
/// 在调用设备发现 API 前检查 Wi-Fi Direct 发现能力。
/// </summary>
public sealed class WifiDirectCapabilityChecker
{
    public WifiDirectAvailability Check()
    {
        try
        {
            if (!PeerFinder.AllowWiFiDirect)
            {
                PeerFinder.AllowWiFiDirect = true;
            }

            if (!PeerFinder.AllowWiFiDirect)
            {
                return Unsupported(WifiDirectErrorCode.WifiDirectNotSupported);
            }
        }
        catch (Exception exception)
        {
            return Unsupported(MapCapabilityError(exception));
        }

        try
        {
            var discoveryTypes = PeerFinder.SupportedDiscoveryTypes;
            var browseSupported = (discoveryTypes & PeerDiscoveryTypes.Browse) == PeerDiscoveryTypes.Browse;

            return browseSupported
                ? WifiDirectAvailability.Supported()
                : Unsupported(WifiDirectErrorCode.BrowseDiscoveryUnsupported);
        }
        catch (Exception)
        {
            return Unsupported(WifiDirectErrorCode.BrowseDiscoveryUnsupported);
        }
    }

    private static WifiDirectErrorCode MapCapabilityError(Exception exception)
    {
        var errorCode = WifiDirectErrorMapper.Map(
            exception,
            WifiDirectOperation.None,
            WifiDirectErrorContext.WinRt);

        return errorCode == WifiDirectErrorCode.Unknown
            ? WifiDirectErrorCode.WifiDirectNotSupported
            : errorCode;
    }

    private static WifiDirectAvailability Unsupported(WifiDirectErrorCode errorCode) =>
        WifiDirectAvailability.Unsupported(WifiDirectErrorMapper.GetUserMessage(errorCode));
}
