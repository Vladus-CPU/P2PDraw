using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using P2PDraw.App.ViewModels;

namespace P2PDraw.App.Views;

public partial class LayersPanel : UserControl
{
    private readonly Flyout layerActions;
    private TopLevel? topLevel;
    private bool restoreRenameField;
    public event Action? CloseRequested;
    public event Action? ClearRequested;
    public event Action? RemoveRequested;
    private LayersViewModel? Model => DataContext as LayersViewModel;

    public LayersPanel()
    {
        InitializeComponent();
        layerActions = (Flyout)MoreLayerButton.Flyout!;
        SizeChanged += (_, eventArgs) => LayerHint.IsVisible = eventArgs.NewSize.Height >= 300;
        layerActions.Opened += (_, _) => MoreLayerButton.Classes.Add("selected");
        layerActions.Closed += (_, _) => MoreLayerButton.Classes.Remove("selected");
        LayerActionsScroll.LayoutUpdated += (_, _) =>
        {
            if (restoreRenameField)
            {
                restoreRenameField = false;
                if (LayerNameBox.IsFocused)
                {
                    LayerNameBox.BringIntoView();
                }
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            topLevel.SizeChanged += OnViewportSizeChanged;
        }
        UpdateMenuSize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        CloseMenu();
        if (topLevel is not null)
        {
            topLevel.SizeChanged -= OnViewportSizeChanged;
            topLevel = null;
        }
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs eventArgs) => UpdateMenuSize();

    private void OnLayerSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (LayerList.SelectedItem is LayerEntry layer && Model is not null)
        {
            Model.SelectedLayer = layer;
        }
    }

    private void OnAddClicked(object? sender, RoutedEventArgs eventArgs)
    {
        Model?.AddLayer();
        CloseMenu();
    }

    private void OnRenameClicked(object? sender, RoutedEventArgs eventArgs)
    {
        Model?.RenameLayer();
        CloseMenu();
    }

    private void OnRaiseClicked(object? sender, RoutedEventArgs eventArgs) => Model?.MoveLayer(true);
    private void OnLowerClicked(object? sender, RoutedEventArgs eventArgs) => Model?.MoveLayer(false);

    private void OnMoveObjectClicked(object? sender, RoutedEventArgs eventArgs)
    {
        Model?.MoveSelectedObject();
        CloseMenu();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMenu();
        CloseRequested?.Invoke();
    }

    private void OnClearClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMenu();
        ClearRequested?.Invoke();
    }

    private void OnRemoveClicked(object? sender, RoutedEventArgs eventArgs)
    {
        CloseMenu();
        RemoveRequested?.Invoke();
    }

    public bool CloseMenu()
    {
        if (!layerActions.IsOpen)
        {
            return false;
        }
        layerActions.Hide();
        return true;
    }

    private void UpdateMenuSize()
    {
        var available = topLevel?.Bounds.Size ?? Bounds.Size;
        LayerActionsScroll.Width = Math.Max(0, Math.Min(280, available.Width - 48));
        LayerActionsScroll.MaxHeight = Math.Max(0, available.Height - 64);
        restoreRenameField = true;
    }

    private void OnVisibilityClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: Guid layerId })
        {
            Model?.ToggleVisibility(layerId);
        }
    }

    private void OnLockClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: Guid layerId })
        {
            Model?.ToggleLock(layerId);
        }
    }
}
