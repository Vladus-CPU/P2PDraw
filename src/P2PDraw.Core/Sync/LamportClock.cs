namespace P2PDraw.Core.Sync;

public sealed class LamportClock
{
    public long Value
    {
        get; private set;
    }

    public long Tick()
    {
        Value = checked(Value + 1);
        return Value;
    }

    public long Receive(long receivedTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(receivedTime);
        Value = checked(Math.Max(Value, receivedTime) + 1);
        return Value;
    }
}
