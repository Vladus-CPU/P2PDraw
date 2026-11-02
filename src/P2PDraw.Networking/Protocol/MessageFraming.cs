using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2PDraw.Networking.Protocol;

public static class MessageFraming
{
    public const int MaxMessageSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static bool FitsInFrame<T>(T message) where T : class
    {
        int length = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions).Length;
        return length > 0 && length <= MaxMessageSize;
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default) where T : class
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length == 0 || payload.Length > MaxMessageSize)
        {
            throw new InvalidDataException("Message exceeds the size limit.");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : class
    {
        byte[] header = new byte[4];
        int bytesRead = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (bytesRead == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxMessageSize)
        {
            throw new InvalidDataException("Invalid message length.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        try
        {
            var message = JsonSerializer.Deserialize<T>(payload, JsonOptions);
            if (message is null)
            {
                throw new InvalidDataException("Null message.");
            }
            return message;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid JSON message.", exception);
        }
    }
}
