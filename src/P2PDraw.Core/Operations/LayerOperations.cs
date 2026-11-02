namespace P2PDraw.Core.Operations;

public sealed record LayerAdd(OperationStamp Stamp, Guid LayerId, string Name, double Position) : Operation(Stamp);
public sealed record LayerRename(OperationStamp Stamp, Guid LayerId, string Name) : Operation(Stamp);
public sealed record LayerReorder(OperationStamp Stamp, Guid LayerId, double Position) : Operation(Stamp);
public sealed record LayerRemove(OperationStamp Stamp, Guid LayerId) : Operation(Stamp);
public sealed record ObjectLayer(OperationStamp Stamp, Guid ObjectId, Guid LayerId) : Operation(Stamp);
