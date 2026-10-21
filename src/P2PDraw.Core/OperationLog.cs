using System.Collections.Immutable;

namespace P2PDraw.Core;

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

    public ImmutableArray<Operation> GetOperations() => operations.Values.OrderBy(op => op.Stamp).ToImmutableArray();

    public ImmutableArray<Operation> GetMissingOperations(IEnumerable<Guid> knownOperationIds)
    {
        var known = knownOperationIds.ToHashSet();
        return GetOperations().Where(op => !known.Contains(op.Stamp.OperationId)).ToImmutableArray();
    }

    public long GetLastSequence(Guid peerId) => sequences.Keys.Where(key => key.PeerId == peerId).Select(key => key.SequenceNumber).DefaultIfEmpty().Max();
}
