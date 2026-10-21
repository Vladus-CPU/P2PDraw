using P2PDraw.Networking.Protocol;
using System.Buffers.Binary;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class MessageFramingTests
{
    private sealed record Sample(string Text);

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    [Fact]
    public async Task PartialReadsKeepConsecutiveUnicodeMessagesSeparate()
    {
        using var output = new MemoryStream();
        await MessageFraming.WriteAsync(output, new Sample("Привіт"), TestContext.Current.CancellationToken);
        await MessageFraming.WriteAsync(output, new Sample("Другий штрих"), TestContext.Current.CancellationToken);
        byte[] data = output.ToArray();
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("{\"Text\":\"\\u041F\\u0440\\u0438\\u0432\\u0456\\u0442\"}"), BinaryPrimitives.ReadInt32BigEndian(data));
        using var input = new FragmentedStream(data);
        Assert.Equal("Привіт", (await MessageFraming.ReadAsync<Sample>(input, TestContext.Current.CancellationToken))!.Text);
        Assert.Equal("Другий штрих", (await MessageFraming.ReadAsync<Sample>(input, TestContext.Current.CancellationToken))!.Text);
        Assert.Null(await MessageFraming.ReadAsync<Sample>(input, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MessageFraming.MaxMessageSize + 1)]
    public async Task InvalidLengthsAreRejectedBeforeReadingPayload(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync<Sample>(input, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task TruncatedHeaderOrPayloadIsRejected(int length)
    {
        using var output = new MemoryStream();
        await MessageFraming.WriteAsync(output, new Sample("test"), TestContext.Current.CancellationToken);
        using var input = new FragmentedStream(output.ToArray()[..length]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => MessageFraming.ReadAsync<Sample>(input, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"Text\":\"test\",\"Unexpected\":true}")]
    public async Task InvalidJsonIsRejected(string json)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] data = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(data, payload.Length);
        payload.CopyTo(data, 4);
        using var input = new MemoryStream(data);
        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync<Sample>(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedWriteDoesNotWriteHeader()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.WriteAsync(output, new Sample(new string('x', MessageFraming.MaxMessageSize)), TestContext.Current.CancellationToken));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CancelledReadPropagatesCancellation()
    {
        using var input = new MemoryStream([0]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MessageFraming.ReadAsync<Sample>(input, cancellation.Token));
    }
}
