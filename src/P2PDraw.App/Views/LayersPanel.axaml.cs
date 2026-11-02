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

    public LayersPanel() => InitializeComponent();

    private void OnAddClicked(object? sender, RoutedEventArgs eventArgs) => Model?.AddLayer();
    private void OnRenameClicked(object? sender, RoutedEventArgs eventArgs) => Model?.RenameLayer();
    private void OnRaiseClicked(object? sender, RoutedEventArgs eventArgs) => Model?.MoveLayer(true);
    private void OnLowerClicked(object? sender, RoutedEventArgs eventArgs) => Model?.MoveLayer(false);
    private void OnMoveObjectClicked(object? sender, RoutedEventArgs eventArgs) => Model?.MoveSelectedObject();
    private void OnCloseClicked(object? sender, RoutedEventArgs eventArgs) => CloseRequested?.Invoke();
    private void OnClearClicked(object? sender, RoutedEventArgs eventArgs) => ClearRequested?.Invoke();
    private void OnRemoveClicked(object? sender, RoutedEventArgs eventArgs) => RemoveRequested?.Invoke();

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
