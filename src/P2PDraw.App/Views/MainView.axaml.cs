using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using P2PDraw.App.Controls;
using P2PDraw.App.ViewModels;

namespace P2PDraw.App.Views;

public partial class MainView : UserControl
{
    private bool? isCompact;
    private Action? confirmedAction;

    public MainView()
    {
        InitializeComponent();
        DrawingSurface.StrokeStarted += point => Model?.BeginStroke(point);
        DrawingSurface.StrokeContinued += point => Model?.AppendStroke(point);
        DrawingSurface.StrokeFinished += () => Model?.EndStroke();
        DrawingSurface.ViewChanged += () => ResetZoomButton.Content = $"{DrawingSurface.Zoom:P0}";
        DrawingSurface.ShapeCreated += (kind, start, end) => Model?.AddShape(kind, start, end);
        DrawingSurface.ObjectMoved += (objectId, offset) => Model?.MoveObject(objectId, offset);
        DrawingSurface.ObjectDeleted += objectId => Model?.RemoveObject(objectId);
        DrawingSurface.SelectionChanged += selected => DeleteButton.IsEnabled = selected is not null;
        SizeChanged += OnSizeChanged;
        BodyGrid.SizeChanged += (_, eventArgs) =>
        {
            NetworkPanel.MaxHeight = eventArgs.NewSize.Height;
            NetworkScroll.MaxHeight = Math.Max(0, Math.Min(440, eventArgs.NewSize.Height - 96));
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        bool compact = eventArgs.NewSize.Width < 900;
        if (isCompact == compact)
        {
            return;
        }
        isCompact = compact;
        RootGrid.Margin = new Thickness(compact ? 8 : 16);
        AppTitle.FontSize = compact ? 21 : 27;
        NetworkPanel.Padding = new Thickness(compact ? 4 : 14);
        BrushPanel.Padding = new Thickness(compact ? 8 : 14);
        NetworkScroll.MaxHeight = compact ? 260 : 440;
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void OnToolClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button button || button.Tag is not string name || !Enum.TryParse<DrawingTool>(name, out var tool))
        {
            return;
        }
        DrawingSurface.Tool = tool;
        foreach (var item in new[] { SelectTool, BrushTool, LineTool, RectangleTool, EllipseTool })
        {
            item.Classes.Set("selected", item == button);
        }
    }

    private void OnDeleteClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DrawingSurface.SelectedObjectId is Guid selected)
        {
            Model?.RemoveObject(selected);
            DrawingSurface.ClearSelection();
        }
    }

    private void OnSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        NetworkPanel.IsVisible = !NetworkPanel.IsVisible;
        NetworkExpander.IsExpanded = NetworkPanel.IsVisible;
    }

    private async void OnCreateSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.CreateSessionAsync();
            if (Model.CanConnect)
            {
                NetworkPanel.IsVisible = false;
            }
        }
    }

    private async void OnSearchSessionsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.SearchSessionsAsync();
        }
    }

    private async void OnJoinSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.JoinSelectedSessionAsync();
            NetworkPanel.IsVisible = Model.ConnectedPeers == 0;
        }
    }

    private async void OnJoinManualClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.JoinManualSessionAsync();
            NetworkPanel.IsVisible = Model.ConnectedPeers == 0;
        }
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs eventArgs) => DrawingSurface.ResetView();

    protected override void OnDataContextChanged(EventArgs eventArgs)
    {
        base.OnDataContextChanged(eventArgs);
        foreach (var entry in BrushColors.Items)
        {
            if (entry is ComboBoxItem item && item.Tag is string value && Color.Parse(value).ToUInt32() == Model?.BrushColor)
            {
                BrushColors.SelectedItem = item;
                break;
            }
        }
    }

    private async void OnListenClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.StartListeningAsync();
        }
    }

    private async void OnConnectClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.ConnectAsync();
        }
    }

    private async void OnConnectDiscoveredClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.ConnectDiscoveredAsync();
        }
    }

    private void OnDisconnectClicked(object? sender, RoutedEventArgs eventArgs) => Model?.Disconnect();
    private void OnClearClicked(object? sender, RoutedEventArgs eventArgs)
    {
        ConfirmAction("Очистити полотно на всіх шарах? Видалення побачать усі учасники сесії, також після перепідключення.", () => Model?.ClearCanvas());
    }

    private void ConfirmAction(string message, Action action)
    {
        confirmedAction = action;
        ConfirmationText.Text = message;
        ConfirmationOverlay.IsVisible = true;
        HeaderGrid.IsEnabled = false;
        BrushPanel.IsEnabled = false;
        BodyGrid.IsEnabled = false;
        FooterGrid.IsEnabled = false;
        CancelActionButton.Focus();
    }

    private void OnCancelActionClicked(object? sender, RoutedEventArgs eventArgs) => CloseConfirmation();

    private void OnConfirmActionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var action = confirmedAction;
        CloseConfirmation();
        action?.Invoke();
    }

    private void CloseConfirmation()
    {
        confirmedAction = null;
        ConfirmationOverlay.IsVisible = false;
        HeaderGrid.IsEnabled = true;
        BrushPanel.IsEnabled = true;
        BodyGrid.IsEnabled = true;
        FooterGrid.IsEnabled = true;
    }

    private void OnColorChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (Model is not null && BrushColors.SelectedItem is ComboBoxItem item && item.Tag is string value)
        {
            Model.BrushColor = Color.Parse(value).ToUInt32();
        }
    }
}
