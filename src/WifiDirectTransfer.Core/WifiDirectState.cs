namespace WifiDirectTransfer.Core;

public enum WifiDirectState
{
    Idle = 0,
    Starting = 1,
    Listening = 2,
    Discovering = 3,
    Connecting = 4,
    Connected = 5,
    Stopping = 6,
    Faulted = 7
}
