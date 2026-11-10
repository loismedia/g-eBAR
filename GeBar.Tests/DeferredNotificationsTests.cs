using GeBar.UI;

namespace GeBar.Tests;

public class DeferredNotificationsTests
{
    [Fact]
    public void BlockedNotificationsWaitAndDuplicatesAreNotRepeated()
    {
        bool allowed = false;
        var shown = new List<string>();
        var notifications = new DeferredNotifications(() => allowed, (title, text) => { shown.Add(text); return true; });
        notifications.Add("Mouse", "Game rate 1000 Hz");
        notifications.Add("Mouse", "Game rate 1000 Hz");
        notifications.Add("Keyboard", "Low battery");
        notifications.Flush();
        Assert.Empty(shown);
        Assert.True(notifications.Pending);

        allowed = true;
        notifications.Flush();
        Assert.Equal(["Game rate 1000 Hz"], shown);
        Assert.True(notifications.Pending);
        notifications.Flush();
        Assert.Equal(["Game rate 1000 Hz", "Low battery"], shown);
        Assert.False(notifications.Pending);
    }

    [Fact]
    public void GameNotificationIsShownImmediatelyOrDiscardedWithoutQueuing()
    {
        bool allowed = false;
        var shown = new List<string>();
        var notifications = new DeferredNotifications(() => allowed, (_, text) => { shown.Add(text); return true; });
        notifications.Add("Mouse", "Game rate 1000 Hz", false);
        allowed = true;
        notifications.Flush();
        Assert.Empty(shown);
        Assert.False(notifications.Pending);

        notifications.Add("Keyboard", "Low battery");
        notifications.Add("Mouse", "Game rate 1000 Hz", false);
        Assert.Equal(["Game rate 1000 Hz"], shown);
        notifications.Flush();
        Assert.Equal(["Game rate 1000 Hz", "Low battery"], shown);
    }

    [Fact]
    public void FailedSendIsRetriedAndQueueIsBounded()
    {
        bool succeeds = false;
        string? shown = null;
        var notifications = new DeferredNotifications(() => true, (title, text) => { shown = text; return succeeds; });
        for (int index = 0; index < 17; index++) notifications.Add("Mouse", index.ToString());
        notifications.Flush();
        Assert.Equal("1", shown);
        Assert.True(notifications.Pending);
        succeeds = true;
        notifications.Flush();
        Assert.Equal("1", shown);
        notifications.Flush();
        Assert.Equal("2", shown);
    }
}
