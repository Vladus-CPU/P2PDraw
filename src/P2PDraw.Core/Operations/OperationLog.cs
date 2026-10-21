using System.Collections.Immutable;

namespace P2PDraw.Core.Operations;

public sealed class OperationLog
{
    private readonly Dictionary<Guid, Operation> operations = [];
    private readonly Dictionary<(Guid PeerId, long SequenceNumber), Guid> sequences = [];

    public int Count => operations.Count;
    public bool Contains(Guid operationId) => operations.ContainsKey(operationId);

    public bool Add(Operation operation)
    {
        operation.Validate();
        if (Contains(operation.Stamp.OperationId)) return false;

        var key = (operation.Stamp.PeerId, operation.Stamp.SequenceNumber);
        if (sequences.ContainsKey(key)) throw new ArgumentException("Sequence number already belongs to another operation.");

        operations.Add(operation.Stamp.OperationId, operation);
        sequences.Add(key, operation.Stamp.OperationId);
        return true;
    }

    public ImmutableArray<Operation> GetOperations() => operations.Values.OrderBy(operation => operation.Stamp).ToImmutableArray();

    public ImmutableArray<Operation> GetMissingOperations(IEnumerable<Guid> knownOperationIds)
    {
        var knownIds = knownOperationIds.ToHashSet();
        var missingOperations = ImmutableArray.CreateBuilder<Operation>();
        foreach (var operation in GetOperations())
        {
            if (!knownIds.Contains(operation.Stamp.OperationId)) missingOperations.Add(operation);
        }
        return missingOperations.ToImmutable();
    }

    public long GetLastSequence(Guid peerId)
    {
        long lastSequence = 0;
        foreach (var key in sequences.Keys)
        {
            if (key.PeerId == peerId) lastSequence = Math.Max(lastSequence, key.SequenceNumber);
        }
        return lastSequence;
    }
}
