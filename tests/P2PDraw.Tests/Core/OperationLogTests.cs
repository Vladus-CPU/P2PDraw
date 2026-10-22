using P2PDraw.Core.Operations;
using Xunit;

namespace P2PDraw.Tests.Core;

public class OperationLogTests
{
    [Fact]
    public void DuplicateOperationIsIgnored()
    {
        var log = new OperationLog();
        var operation = new StrokeBegin(new(Guid.NewGuid(), Guid.NewGuid(), 1, 1), Guid.NewGuid(), 0, 1);
        Assert.True(log.Add(operation));
        Assert.False(log.Add(operation));
        Assert.Single(log.GetOperations());
    }

    [Fact]
    public void MissingOperationsIncludeSequenceGaps()
    {
        var log = new OperationLog();
        var peer = Guid.NewGuid();
        var operations = new StrokeBegin[3];
        for (int i = 0; i < operations.Length; i++)
        {
            var stamp = new OperationStamp(Guid.NewGuid(), peer, i + 1, i + 1);
            operations[i] = new StrokeBegin(stamp, Guid.NewGuid(), 0, 1);
        }
        foreach (var operation in operations.Reverse())
        {
            log.Add(operation);
        }

        var missing = log.GetMissingOperations([operations[0].Stamp.OperationId, operations[2].Stamp.OperationId]);
        Assert.Equal(operations[1], Assert.Single(missing));
        Assert.Equal(3, log.GetLastSequence(peer));
        Assert.Equal(0, log.GetLastSequence(Guid.NewGuid()));
        Assert.Equal(operations, log.GetOperations());
    }

    [Fact]
    public void ReusedSequenceIsRejectedWithoutChangingLog()
    {
        var log = new OperationLog();
        var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
        log.Add(new StrokeBegin(stamp, Guid.NewGuid(), 0, 1));
        var other = new StrokeBegin(stamp with { OperationId = Guid.NewGuid() }, Guid.NewGuid(), 0, 1);
        Assert.Throws<ArgumentException>(() => log.Add(other));
        Assert.Single(log.GetOperations());
    }

    [Fact]
    public void InvalidOperationDoesNotEnterLog()
    {
        var log = new OperationLog();
        Assert.Throws<ArgumentException>(() => log.Add(new StrokeEnd(default, Guid.NewGuid(), 0)));
        Assert.Equal(0, log.Count);
    }
}
