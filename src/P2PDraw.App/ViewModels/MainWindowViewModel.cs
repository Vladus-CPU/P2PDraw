using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using P2PDraw.App.Editing;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Discovery;

namespace P2PDraw.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private Guid? activeStrokeId;
    private uint brushColor = 0xFF253449;
    private double brushWidth = 3;
    private bool fillShapes;
    private bool isDisposed;
    private readonly ObjectDuplicator duplicator;
    private readonly EditHistory history;
    private SyncEngine Engine => Session.Engine;

    public event PropertyChangedEventHandler? PropertyChanged;
    public SessionViewModel Session { get; }
    public LayersViewModel LayerEditor { get; }
    public ImmutableArray<Stroke> Strokes { get; private set; } = [];
    public ImmutableArray<Shape> Shapes { get; private set; } = [];
    public int StrokeCount => Strokes.Length;
    public int ObjectCount => Strokes.Length + Shapes.Length;
    public int OperationCount { get; private set; }
    public bool CanUndo => !isDisposed && history.CanUndo;
    public bool CanRedo => !isDisposed && history.CanRedo;

    public uint BrushColor
    {
        get => brushColor;
        set
        {
            brushColor = value;
            Notify();
        }
    }

    public double BrushWidth
    {
        get => brushWidth;
        set
        {
            brushWidth = value;
            Notify();
        }
    }

    public bool FillShapes
    {
        get => fillShapes;
        set
        {
            fillShapes = value;
            Notify();
        }
    }

    // Existing bindings keep their names; Session owns connection state.
    public string SessionId
    {
        get => Session.SessionId;
        set => Session.SessionId = value;
    }
    public string LocalPort
    {
        get => Session.LocalPort;
        set => Session.LocalPort = value;
    }
    public string RemoteAddress
    {
        get => Session.RemoteAddress;
        set => Session.RemoteAddress = value;
    }
    public string RemotePort
    {
        get => Session.RemotePort;
        set => Session.RemotePort = value;
    }
    public string SessionTitle => Session.SessionTitle;
    public string ConnectionStatus => Session.ConnectionStatus;
    public string DiscoveryStatus => Session.DiscoveryStatus;
    public int ConnectedPeers => Session.ConnectedPeers;
    public bool CanEditSession => Session.CanEditSession;
    public bool CanStartListening => Session.CanStartListening;
    public bool CanConnect => Session.CanConnect;
    public bool CanDisconnect => Session.CanDisconnect;
    public bool CanConnectDiscovered => Session.CanConnectDiscovered;
    public bool CanCreateSession => Session.CanCreateSession;
    public bool CanSearchSessions => Session.CanSearchSessions;
    public bool CanJoinSession => Session.CanJoinSession;
    public ImmutableArray<DiscoveredPeer> DiscoveredPeers => Session.DiscoveredPeers;
    public ImmutableArray<DiscoveredPeer> AvailableSessions => Session.AvailableSessions;
    public DiscoveredPeer? SelectedPeer
    {
        get => Session.SelectedPeer;
        set => Session.SelectedPeer = value;
    }
    public DiscoveredPeer? SelectedSession
    {
        get => Session.SelectedSession;
        set => Session.SelectedSession = value;
    }
    public string Status
    {
        get => Session.Status;
        private set => Session.Status = value;
    }

    public MainWindowViewModel(string[]? args = null)
    {
        Session = new SessionViewModel(args);
        history = new EditHistory(() => Engine, Session.PublishOperation);
        history.Changed += () =>
        {
            Notify(nameof(CanUndo));
            Notify(nameof(CanRedo));
        };
        LayerEditor = new LayersViewModel(() => Engine, Session.EnsureSession, operation =>
        {
            history.DiscardRedo();
            Session.PublishOperation(operation);
        });
        LayerEditor.Error += message => Status = message;
        duplicator = new ObjectDuplicator(() => Engine, Session.PublishOperation);
        Session.PropertyChanged += OnSessionPropertyChanged;
        Session.DocumentChanged += RefreshDocument;
    }

    public Task CreateSessionAsync() => Session.CreateSessionAsync();
    public Task SearchSessionsAsync() => Session.SearchSessionsAsync();
    public Task JoinSelectedSessionAsync() => Session.JoinSelectedSessionAsync();
    public Task JoinManualSessionAsync() => Session.JoinManualSessionAsync();
    public Task StartListeningAsync() => Session.StartListeningAsync();
    public Task ConnectAsync() => Session.ConnectAsync();
    public Task ConnectDiscoveredAsync() => Session.ConnectDiscoveredAsync();
    public void Disconnect() => Session.Disconnect();
    public void ResumeNetworking() => Session.ResumeNetworking();

    public bool BeginStroke(StrokePoint point)
    {
        if (isDisposed || !LayerEditor.CanDraw || !Session.EnsureSession())
        {
            return false;
        }
        EndStroke();
        try
        {
            var begin = Engine.BeginStroke(BrushColor, BrushWidth, LayerEditor.ActiveLayerId);
            activeStrokeId = begin.StrokeId;
            Session.PublishOperation(begin);
            AppendStroke(point);
            return true;
        }
        catch (InvalidOperationException)
        {
            activeStrokeId = null;
            RefreshDocument();
            Status = "Активний шар видалено іншим учасником. Виберіть інший шар.";
            return false;
        }
    }

    public void AppendStroke(StrokePoint point)
    {
        if (activeStrokeId is null || isDisposed)
        {
            return;
        }
        try
        {
            Session.PublishOperation(Engine.AppendStroke(activeStrokeId.Value, [point]));
        }
        catch (InvalidOperationException)
        {
            // A remote clear can remove the stroke while the pointer is still captured.
            activeStrokeId = null;
            RefreshDocument();
        }
    }

    public void EndStroke()
    {
        if (activeStrokeId is null || isDisposed)
        {
            return;
        }
        try
        {
            Session.PublishOperation(Engine.EndStroke(activeStrokeId.Value));
            history.RecordCreation(activeStrokeId.Value);
        }
        catch (InvalidOperationException)
        {
            RefreshDocument();
        }
        activeStrokeId = null;
    }

    public void AddShape(ShapeKind kind, StrokePoint start, StrokePoint end)
    {
        if (isDisposed || !LayerEditor.CanDraw || !Session.EnsureSession())
        {
            return;
        }
        EndStroke();
        uint? fill = FillShapes && kind != ShapeKind.Line ? BrushColor : null;
        try
        {
            var added = Engine.AddShape(kind, start, end, BrushColor, BrushWidth, fill, LayerEditor.ActiveLayerId);
            Session.PublishOperation(added);
            history.RecordCreation(added.ShapeId);
        }
        catch (ArgumentException)
        {
            Status = "Не вдалося створити фігуру. Перевірте її координати та товщину.";
        }
        catch (InvalidOperationException)
        {
            RefreshDocument();
            Status = "Активний шар видалено іншим учасником. Виберіть інший шар.";
        }
    }

    public void MoveObject(Guid objectId, StrokePoint offset)
    {
        if (isDisposed || !Session.EnsureSession())
        {
            return;
        }
        try
        {
            var moved = Engine.MoveObject(objectId, offset);
            Session.PublishOperation(moved);
            history.RecordMove(moved);
        }
        catch (InvalidOperationException)
        {
            Status = "Об'єкт уже видалено або змінено іншим учасником.";
            RefreshDocument();
        }
        catch (ArgumentException)
        {
            Status = "Не вдалося перемістити об'єкт. Перевірте координати.";
        }
    }

    public void RemoveObject(Guid objectId)
    {
        if (isDisposed || !Session.EnsureSession())
        {
            return;
        }
        try
        {
            history.DiscardRedo();
            Session.PublishOperation(Engine.RemoveObject(objectId));
        }
        catch (ArgumentException)
        {
            Status = "Не вдалося видалити вибраний об'єкт.";
        }
    }

    public void ResizeShape(Guid shapeId, StrokePoint start, StrokePoint end)
    {
        if (isDisposed || !Session.EnsureSession())
        {
            return;
        }
        try
        {
            var resized = Engine.ResizeShape(shapeId, start, end);
            Session.PublishOperation(resized);
            history.RecordResize(resized);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
        {
            Status = "Не вдалося змінити розмір: фігуру видалено або координати некоректні.";
            RefreshDocument();
        }
    }

    public void DuplicateObject(Guid objectId)
    {
        if (isDisposed || !Session.EnsureSession())
        {
            return;
        }
        try
        {
            var displacement = new StrokePoint(16, 16);
            var shape = Shapes.FirstOrDefault(item => item.ShapeId == objectId);
            var stroke = Strokes.FirstOrDefault(item => item.StrokeId == objectId);
            if (shape is not null)
            {
                Guid copyId = duplicator.Copy(shape, displacement);
                history.RecordCreation(copyId);
            }
            else if (stroke is not null)
            {
                Guid copyId = duplicator.Copy(stroke, displacement);
                history.RecordCreation(copyId);
            }
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
        {
            Status = "Не вдалося дублювати об'єкт: шар уже видалено.";
        }
    }

    public void ClearCanvas()
    {
        if (isDisposed || !Session.EnsureSession())
        {
            return;
        }
        EndStroke();
        history.DiscardRedo();
        Session.PublishOperation(Engine.ClearCanvas());
        Status = "Відомі об'єкти видалено.";
    }

    public Snapshot GetSnapshot() => Engine.CreateSnapshot();

    public async Task<bool> OpenAsNewSessionAsync(Snapshot snapshot)
    {
        EndStroke();
        if (!await Session.OpenAsNewSessionAsync(snapshot))
        {
            return false;
        }
        activeStrokeId = null;
        history.Clear();
        LayerEditor.ResetViewState();
        RefreshDocument();
        return true;
    }

    public void Undo()
    {
        EndStroke();
        if (!history.Undo())
        {
            Status = "Дію пропущено: об'єкт змінено іншим учасником або видалено.";
        }
    }

    public void Redo()
    {
        EndStroke();
        if (!history.Redo())
        {
            Status = "Повторення пропущено: об'єкт чи шар уже змінено або видалено.";
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        Notify(eventArgs.PropertyName);
    }

    private void RefreshDocument()
    {
        if (isDisposed)
        {
            return;
        }
        var snapshot = Engine.CreateSnapshot();
        Strokes = snapshot.Strokes;
        Shapes = snapshot.Shapes;
        OperationCount = snapshot.Operations.Length;
        LayerEditor.Refresh();
        Notify(nameof(Strokes));
        Notify(nameof(Shapes));
        Notify(nameof(StrokeCount));
        Notify(nameof(ObjectCount));
        Notify(nameof(OperationCount));
    }

    private void Notify([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }
        EndStroke();
        isDisposed = true;
        Session.PropertyChanged -= OnSessionPropertyChanged;
        Session.DocumentChanged -= RefreshDocument;
        await Session.DisposeAsync();
    }
}
