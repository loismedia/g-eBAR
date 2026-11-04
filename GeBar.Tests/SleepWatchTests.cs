using GeBar.Hid;

namespace GeBar.Tests;

public class SleepWatchTests
{
    [Fact]
    public void SleepMarkerSurvivesChangingPacketTails()
    {
        var capture = new SleepWatch.Capture();
        capture.Add("mouse", [0, 0x10, 0x20, 1], SleepWatchPhase.Idle);
        capture.Add("mouse", [0, 0x10, 0x20, 2], SleepWatchPhase.Idle);
        capture.Add("mouse", [0, 0x30, 0x40, 3], SleepWatchPhase.Sleeping);
        capture.Add("mouse", [0, 0x30, 0x40, 4], SleepWatchPhase.Sleeping);
        capture.Add("mouse", [0, 0x10, 0x20, 5], SleepWatchPhase.Waking);

        SleepWatchResult result = capture.Analyse(1);

        Assert.Equal("mouse", result.DevicePath);
        Assert.Equal("3040", result.OffPrefixHex);
        Assert.Equal(5, result.Packets);
    }

    [Fact]
    public void PrefixSeenAfterWakingIsNotASleepMarker()
    {
        var capture = new SleepWatch.Capture();
        capture.Add("mouse", [0, 0x10, 0x20], SleepWatchPhase.Idle);
        capture.Add("mouse", [0, 0x30, 0x40], SleepWatchPhase.Sleeping);
        capture.Add("mouse", [0, 0x30, 0x40], SleepWatchPhase.Waking);

        Assert.Null(capture.Analyse(1).OffPrefixHex);
        Assert.Empty(capture.Analyse(1).Candidates);
    }

    [Fact]
    public void AwakePrefixExcludesSleepPacketWithADifferentReportId()
    {
        var capture = new SleepWatch.Capture();
        capture.Add("mouse", [1, 0x30, 0x40], SleepWatchPhase.Idle);
        capture.Add("mouse", [2, 0x30, 0x40], SleepWatchPhase.Sleeping);

        Assert.Empty(capture.Analyse(1).Candidates);
    }

    [Fact]
    public void IdenticalSleepMarkersOnTwoInterfacesRemainAmbiguous()
    {
        var capture = new SleepWatch.Capture();
        capture.Add("first", [0, 0x30, 0x40], SleepWatchPhase.Sleeping);
        capture.Add("second", [0, 0x30, 0x40], SleepWatchPhase.Sleeping);

        SleepWatchResult result = capture.Analyse(2);

        Assert.Null(result.OffPrefixHex);
        Assert.Equal("3040", Assert.Single(result.Candidates));
    }

    [Fact]
    public void UnstableOrTooShortPrefixesAreIgnored()
    {
        var capture = new SleepWatch.Capture();
        capture.Add("mouse", [0, 0x30, 0x40], SleepWatchPhase.Sleeping);
        capture.Add("mouse", [0, 0x30, 0x50], SleepWatchPhase.Sleeping);

        Assert.Empty(capture.Analyse(1).Candidates);
    }

    [Fact]
    public void FrequentPacketsDoNotAllocateARecordForEverySample()
    {
        var capture = new SleepWatch.Capture();
        byte[] packet = [0, 0x30, 0x40, 0];
        capture.Add("mouse", packet, SleepWatchPhase.Sleeping);
        packet[^1] = 1;
        capture.Add("mouse", packet, SleepWatchPhase.Sleeping);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 100_000; index++)
        {
            packet[^1] = (byte)index;
            capture.Add("mouse", packet, SleepWatchPhase.Sleeping);
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4096);
        Assert.Equal(100_002, capture.Packets);
        Assert.Equal("3040", capture.Analyse(1).OffPrefixHex);
    }
}
