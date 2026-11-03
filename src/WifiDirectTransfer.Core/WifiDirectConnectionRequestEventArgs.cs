namespace WifiDirectTransfer.Core;

public sealed class WifiDirectConnectionRequestEventArgs : EventArgs
{
    public WifiDirectConnectionRequestEventArgs(IWifiDirectConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    public IWifiDirectConnectionRequest Request { get; }
}
