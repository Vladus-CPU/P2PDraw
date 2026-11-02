using Avalonia.Controls;
using Avalonia.Interactivity;
using P2PDraw.App.ViewModels;

namespace P2PDraw.App.Views;

public partial class LayersPanel : UserControl
{
    public event Action? CloseRequested;
    public event Action? ClearRequested;
    public event Action? RemoveRequested;
    private LayersViewModel? Model => DataContext as LayersViewModel;

    public LayersPanel()
    {
        InitializeComponent();
        SizeChanged += (_, eventArgs) => LayerHint.IsVisible = eventArgs.NewSize.Height >= 300;
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

    private void OnMoreClicked(object? sender, RoutedEventArgs eventArgs)
    {
        bool showActions = !LayerActionsScroll.IsVisible;
        LayerActionsScroll.IsVisible = showActions;
        LayerList.IsVisible = !showActions;
        MoreLayerButton.Classes.Set("selected", showActions);
    }

    public bool CloseMenu()
    {
        if (!LayerActionsScroll.IsVisible)
        {
            return false;
        }
        LayerActionsScroll.IsVisible = false;
        LayerList.IsVisible = true;
        MoreLayerButton.Classes.Remove("selected");
        return true;
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
