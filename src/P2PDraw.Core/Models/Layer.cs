using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Models;

public sealed record Layer(Guid LayerId, string Name, double Position, OperationStamp? CreatedAt = null)
{
    public static readonly Guid DefaultLayerId = new("00000000-0000-0000-0000-000000000001");
    public static readonly Layer Default = new(DefaultLayerId, "Шар 1", 0);
    public OperationStamp? NameChangedAt { get; init; }
    public OperationStamp? OrderChangedAt { get; init; }

    internal static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 100;
}
