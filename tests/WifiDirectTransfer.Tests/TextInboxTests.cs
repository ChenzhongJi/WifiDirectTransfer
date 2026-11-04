using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class TextInboxTests
{
    [Fact]
    public void Add_KeepsNewestFirst_AndTrimsToMaxMessages()
    {
        var inbox = new TextInbox();

        for (var index = 0; index < TextInbox.MaxMessages + 5; index++)
        {
            inbox.Add(
                new ReceivedTextMessage(
                    DateTimeOffset.Now.AddSeconds(index),
                    "peer",
                    $"msg-{index}"));
        }

        var messages = inbox.Messages;

        Assert.Equal(TextInbox.MaxMessages, messages.Count);
        Assert.Equal(
            $"msg-{TextInbox.MaxMessages + 4}",
            messages[0].Text);
        Assert.DoesNotContain(
            messages,
            message => message.Text == "msg-0");
        Assert.Equal(TextInbox.MaxMessages, inbox.UnreadCount);
    }

    [Fact]
    public void UnreadCount_TracksMessages_AndMarkAllRead()
    {
        var inbox = new TextInbox();

        inbox.Add(new ReceivedTextMessage(DateTimeOffset.Now, "peer", "a"));
        inbox.Add(new ReceivedTextMessage(DateTimeOffset.Now, "peer", "b"));

        Assert.Equal(2, inbox.UnreadCount);

        inbox.MarkAllRead();

        Assert.Equal(0, inbox.UnreadCount);
        Assert.Equal(2, inbox.Messages.Count);
    }

    [Fact]
    public void Clear_RemovesMessagesAndUnreadCount()
    {
        var inbox = new TextInbox();
        inbox.Add(new ReceivedTextMessage(DateTimeOffset.Now, "peer", "a"));
        inbox.MarkAllRead();
        inbox.Add(new ReceivedTextMessage(DateTimeOffset.Now, "peer", "b"));

        inbox.Clear();

        Assert.Empty(inbox.Messages);
        Assert.Equal(0, inbox.UnreadCount);
    }
}
