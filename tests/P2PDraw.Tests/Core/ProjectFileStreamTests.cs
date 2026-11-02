using System.Text;
using System.Text.Json;
using P2PDraw.Core.Storage;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class ProjectFileStreamTests
{
    [Fact]
    public async Task ReadSupportsPartialReadsWithoutSeekingOrLength()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var project = ProjectFile.Create("Потоковий документ", engine.CreateSnapshot());
        using var stream = new ReadStream(Encoding.UTF8.GetBytes(project.Serialize()));

        var restored = await ProjectFile.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(project.Name, restored.Name);
        Assert.Equal(JsonSerializer.Serialize(project.Snapshot), JsonSerializer.Serialize(restored.Snapshot));
        Assert.True(stream.ReadCalls > 1);
        Assert.False(stream.IsDisposed);
    }

    [Fact]
    public async Task ReadRejectsOversizedStreamWithoutReadingLength()
    {
        using var stream = new ReadStream([], ProjectFile.MaximumFileBytes + 1);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProjectFile.ReadAsync(stream, TestContext.Current.CancellationToken));

        Assert.Contains("32 МіБ", error.Message);
        Assert.False(stream.IsDisposed);
    }

    [Fact]
    public async Task ReadHonorsCancellationBeforeFirstRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new ReadStream([]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectFile.ReadAsync(stream, cancellation.Token));

        Assert.Equal(0, stream.ReadCalls);
    }

    [Fact]
    public async Task ReadHonorsCancellationBetweenPartialReads()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ReadStream(Encoding.UTF8.GetBytes("{\"FormatVersion\":1}")) { AfterRead = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectFile.ReadAsync(stream, cancellation.Token));

        Assert.Equal(1, stream.ReadCalls);
        Assert.False(stream.IsDisposed);
    }

    [Fact]
    public async Task ReadRejectsTruncatedJson()
    {
        using var stream = new ReadStream(Encoding.UTF8.GetBytes("{\"FormatVersion\":1"));

        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectFile.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    private sealed class ReadStream(byte[] contents, long? generatedLength = null) : Stream
    {
        private long position;
        public int ReadCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public Action? AfterRead { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            long remaining = (generatedLength ?? contents.Length) - position;
            int chunkLimit = generatedLength is null ? 7 : 81920;
            int count = (int)Math.Min(remaining, Math.Min(buffer.Length, chunkLimit));
            if (generatedLength is null)
            {
                contents.AsMemory((int)position, count).CopyTo(buffer);
            }
            else
            {
                buffer.Span[..count].Fill((byte)' ');
            }
            position += count;
            AfterRead?.Invoke();
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
