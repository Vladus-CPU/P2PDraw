using System.Text.Json.Serialization;
using P2PDraw.Core;

namespace P2PDraw.Networking;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(OperationMessage), "operation")]
public abstract record NetworkMessage;

public sealed record HelloMessage(Guid PeerId, Guid SessionId, int ProtocolVersion) : NetworkMessage;
public sealed record WelcomeMessage(Guid PeerId, Guid SessionId, int ProtocolVersion) : NetworkMessage;
public sealed record OperationMessage(Operation Operation) : NetworkMessage;
