using P2PDraw.Core;
using Xunit;

namespace P2PDraw.Tests;

public class LamportClockTests
{
    [Fact]
    public void LocalEventsIncreaseTime()
    {
        var clock = new LamportClock();
        Assert.Equal(0, clock.Value);
        Assert.Equal(1, clock.Tick());
        Assert.Equal(2, clock.Tick());
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(20, 21)]
    public void ReceiveUsesMaximumTime(long receivedTime, long expected)
    {
        var clock = new LamportClock();
        clock.Tick();
        Assert.Equal(expected, clock.Receive(receivedTime));
        Assert.Equal(expected + 1, clock.Tick());
    }

    [Fact]
    public void InvalidTimeDoesNotChangeClock()
    {
        var clock = new LamportClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Receive(-1));
        Assert.Equal(0, clock.Value);
    }

    [Fact]
    public void OverflowDoesNotWrapClock()
    {
        var clock = new LamportClock();
        clock.Receive(long.MaxValue - 1);
        Assert.Throws<OverflowException>(() => clock.Tick());
        Assert.Throws<OverflowException>(() => clock.Receive(long.MaxValue));
        Assert.Equal(long.MaxValue, clock.Value);
    }
}
