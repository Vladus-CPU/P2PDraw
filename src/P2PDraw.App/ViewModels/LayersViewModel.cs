using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;

namespace P2PDraw.App.ViewModels;

public sealed record LayerEntry(Guid LayerId, string Name, bool IsVisible, bool IsLocked)
{
    public string VisibilityIcon => IsVisible ? "●" : "○";
    public string LockIcon => IsLocked ? "🔒" : "🔓";
}

public sealed class LayersViewModel : INotifyPropertyChanged
{
    private readonly Func<SyncEngine> getEngine;
    private readonly Func<bool> ensureSession;
    private readonly Action<Operation> publish;
    private readonly HashSet<Guid> hidden = [];
    private readonly HashSet<Guid> locked = [];
    private Guid activeLayerId = Layer.DefaultLayerId;
    private Guid? selectedObjectId;
    private string layerName = "Шар 1";
    private LayerEntry? selectedLayer;
    private bool updatingLayers;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? Error;
    public ImmutableArray<LayerEntry> Layers { get; private set; } = [];
    public IReadOnlyList<Guid> VisibleLayerIds { get; private set; } = Array.Empty<Guid>();
    public IReadOnlySet<Guid> SelectableLayerIds { get; private set; } = new HashSet<Guid>();
    public Guid ActiveLayerId => activeLayerId;
    public bool CanDraw => SelectableLayerIds.Contains(activeLayerId);
    public bool HasActiveLayer => activeLayerId != Guid.Empty;
    public bool CanRaise => Layers.Length > 1 && Layers[0].LayerId != activeLayerId && HasActiveLayer;
    public bool CanLower => Layers.Length > 1 && Layers[^1].LayerId != activeLayerId && HasActiveLayer;
    public bool CanMoveObject => selectedObjectId is not null && CanDraw;

    public LayerEntry? SelectedLayer
    {
        get => selectedLayer;
        set
        {
            if (updatingLayers || value is null || value.LayerId == activeLayerId)
            {
                return;
            }
            activeLayerId = value.LayerId;
            layerName = value.Name;
            Refresh();
        }
    }

    public Guid? SelectedObjectId
    {
        get => selectedObjectId;
        set
        {
            selectedObjectId = value;
            Notify(nameof(CanMoveObject));
        }
    }

    public string LayerName
    {
        get => layerName;
        set
        {
            layerName = value;
            Notify();
        }
    }

    public LayersViewModel(Func<SyncEngine> getEngine, Func<bool> ensureSession, Action<Operation> publish)
    {
        this.getEngine = getEngine;
        this.ensureSession = ensureSession;
        this.publish = publish;
        Refresh();
    }

    public void Refresh()
    {
        var documentLayers = getEngine().GetLayers();
        Guid previousId = activeLayerId;
        if (!documentLayers.Any(layer => layer.LayerId == activeLayerId))
        {
            activeLayerId = documentLayers.LastOrDefault()?.LayerId ?? Guid.Empty;
        }
        var entries = ImmutableArray.CreateBuilder<LayerEntry>();
        var visible = new List<Guid>();
        var selectable = new HashSet<Guid>();
        foreach (var layer in documentLayers)
        {
            bool isVisible = !hidden.Contains(layer.LayerId);
            bool isLocked = locked.Contains(layer.LayerId);
            entries.Add(new LayerEntry(layer.LayerId, layer.Name, isVisible, isLocked));
            if (isVisible)
            {
                visible.Add(layer.LayerId);
                if (!isLocked)
                {
                    selectable.Add(layer.LayerId);
                }
            }
        }
        string? previousName = selectedLayer?.Name;
        Layers = entries.ToImmutable().Reverse().ToImmutableArray();
        selectedLayer = Layers.FirstOrDefault(layer => layer.LayerId == activeLayerId);
        if (previousId != activeLayerId || previousName != selectedLayer?.Name)
        {
            layerName = selectedLayer?.Name ?? "";
        }
        VisibleLayerIds = visible;
        SelectableLayerIds = selectable;
        // ListBox can report the old selection while its items are being replaced.
        updatingLayers = true;
        try
        {
            Notify(nameof(Layers));
            Notify(nameof(SelectedLayer));
            Notify(nameof(LayerName));
            Notify(nameof(ActiveLayerId));
            Notify(nameof(VisibleLayerIds));
            Notify(nameof(SelectableLayerIds));
            Notify(nameof(CanDraw));
            Notify(nameof(HasActiveLayer));
            Notify(nameof(CanRaise));
            Notify(nameof(CanLower));
            Notify(nameof(CanMoveObject));
        }
        finally
        {
            updatingLayers = false;
        }
    }

    public void AddLayer()
    {
        if (!ensureSession())
        {
            return;
        }
        var layer = getEngine().AddLayer($"Шар {Layers.Length + 1}");
        activeLayerId = layer.LayerId;
        publish(layer);
        Refresh();
    }

    public void RenameLayer() => Apply(engine => engine.RenameLayer(activeLayerId, LayerName));
    public void RemoveLayer(Guid layerId) => Apply(engine => engine.RemoveLayer(layerId));
    public void ClearLayer(Guid layerId) => Apply(engine => engine.ClearLayer(layerId));

    public void MoveSelectedObject()
    {
        if (CanMoveObject && selectedObjectId is Guid objectId)
        {
            Apply(engine => engine.MoveObjectToLayer(objectId, activeLayerId));
        }
    }

    public void MoveLayer(bool raise)
    {
        if (raise && !CanRaise || !raise && !CanLower)
        {
            return;
        }
        var ordered = getEngine().GetLayers();
        int index = 0;
        while (ordered[index].LayerId != activeLayerId)
        {
            index++;
        }
        int neighborIndex = index + (raise ? 1 : -1);
        int outerIndex = index + (raise ? 2 : -2);
        double position;
        if (outerIndex < 0 || outerIndex >= ordered.Length)
        {
            position = ordered[neighborIndex].Position + (raise ? 1 : -1);
        }
        else
        {
            position = ordered[neighborIndex].Position / 2 + ordered[outerIndex].Position / 2;
        }
        Apply(engine => engine.SetLayerPosition(activeLayerId, position));
    }

    public void ToggleVisibility(Guid layerId)
    {
        if (!hidden.Add(layerId))
        {
            hidden.Remove(layerId);
        }
        Refresh();
    }

    public void ToggleLock(Guid layerId)
    {
        if (!locked.Add(layerId))
        {
            locked.Remove(layerId);
        }
        Refresh();
    }

    public void ResetViewState()
    {
        hidden.Clear();
        locked.Clear();
        selectedObjectId = null;
        activeLayerId = Layer.DefaultLayerId;
        Refresh();
    }

    private void Apply(Func<SyncEngine, Operation> createOperation)
    {
        if (!ensureSession())
        {
            return;
        }
        try
        {
            publish(createOperation(getEngine()));
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
        {
            Error?.Invoke("Не вдалося змінити шар. Перевірте назву та чи шар ще існує.");
        }
        Refresh();
    }

    private void Notify([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
