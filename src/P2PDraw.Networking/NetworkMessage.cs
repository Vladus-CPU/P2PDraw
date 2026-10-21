using System.Text.Json.Serialization;
using P2PDraw.Core;
using System.Collections.Immutable;

namespace P2PDraw.Networking;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(OperationMessage), "operation")]
[JsonDerivedType(typeof(SyncRequestMessage), "syncRequest")]
[JsonDerivedType(typeof(SnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(SyncCompleteMessage), "syncComplete")]
public abstract record NetworkMessage;

public sealed record HelloMessage(Guid PeerId, Guid SessionId, int ProtocolVersion) : NetworkMessage;
public sealed record WelcomeMessage(Guid PeerId, Guid SessionId, int ProtocolVersion) : NetworkMessage;
public sealed record OperationMessage(Operation Operation) : NetworkMessage;
public sealed record SyncRequestMessage(ImmutableArray<Guid> KnownOperationIds) : NetworkMessage;
public sealed record SnapshotMessage(Snapshot Snapshot) : NetworkMessage;
public sealed record SyncCompleteMessage : NetworkMessage;
