namespace WifiDirectTransfer.Core;

public sealed record ReceivedTextMessage(
    DateTimeOffset ReceivedAt,
    string SenderName,
    string Text);

/// <summary>
/// 会话内的文本收件箱。只在内存中保存，最多 100 条。
/// </summary>
public sealed class TextInbox
{
    public const int MaxMessages = 100;

    private readonly object _gate = new();
    private readonly List<ReceivedTextMessage> _messages = new();
    private int _unreadCount;

    public IReadOnlyList<ReceivedTextMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                return _unreadCount;
            }
        }
    }

    public void Add(ReceivedTextMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            _messages.Insert(0, message);

            if (_messages.Count > MaxMessages)
            {
                _messages.RemoveRange(
                    MaxMessages,
                    _messages.Count - MaxMessages);
            }

            _unreadCount = Math.Min(_unreadCount + 1, _messages.Count);
        }
    }

    public void MarkAllRead()
    {
        lock (_gate)
        {
            _unreadCount = 0;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            _unreadCount = 0;
        }
    }
}
