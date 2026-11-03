using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;

namespace P2PDraw.App.Session;

public sealed class EditorSession : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool isDisposed;

    public event Action? Replaced;
    public SyncEngine Engine { get; private set; }
    public SessionConnection Connection { get; }
    public bool IsLocked { get; private set; }
    public bool IsReplacing { get; private set; }

    public EditorSession(Guid sessionId)
    {
        Engine = new SyncEngine(Guid.NewGuid(), sessionId);
        Connection = new SessionConnection(() => Engine);
    }

    public bool EnsureSession(Guid sessionId)
    {
        if (isDisposed || IsReplacing || sessionId == Guid.Empty)
        {
            return false;
        }
        if (IsLocked)
        {
            return true;
        }
        if (Engine.SessionId != sessionId)
        {
            Engine = new SyncEngine(Engine.PeerId, sessionId);
        }
        IsLocked = true;
        return true;
    }

    public SyncEngine? PrepareJoin(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return null;
        }
        if (Engine.SessionId == sessionId)
        {
            return Engine;
        }
        if (!Engine.GetOperations().IsEmpty)
        {
            return null;
        }
        return new SyncEngine(Engine.PeerId, sessionId);
    }

    public Task<SyncEngine> RestoreAsync(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SessionId == Guid.Empty)
        {
            throw new ArgumentException("Document session ID is missing.");
        }
        return Task.Run(() =>
        {
            var restored = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            restored.MergeSnapshot(snapshot with { SessionId = restored.SessionId });
            foreach (var operation in restored.GetOperations())
            {
                if (!MessageFraming.FitsInFrame<NetworkMessage>(new OperationMessage(operation)))
                {
                    throw new ArgumentException("Операція у файлі перевищує 4 МіБ і не може бути синхронізована. Відкриття скасовано.");
                }
            }
            return restored;
        }, lifetime.Token);
    }

    public async Task ReplaceAsync(SyncEngine replacement)
    {
        IsReplacing = true;
        try
        {
            await Connection.StopAsync();
            Engine = replacement;
            IsLocked = !Engine.GetOperations().IsEmpty;
            Replaced?.Invoke();
        }
        finally
        {
            IsReplacing = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        await lifetime.CancelAsync();
        await Connection.DisposeAsync();
        lifetime.Dispose();
    }
}
