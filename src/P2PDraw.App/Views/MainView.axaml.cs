using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using P2PDraw.App.ViewModels;

namespace P2PDraw.App.Views;

public partial class MainView : UserControl
{
    private bool? isCompact;

    public MainView()
    {
        InitializeComponent();
        DrawingSurface.StrokeStarted += point => Model?.BeginStroke(point);
        DrawingSurface.StrokeContinued += point => Model?.AppendStroke(point);
        DrawingSurface.StrokeFinished += () => Model?.EndStroke();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        bool compact = eventArgs.NewSize.Width < 900;
        if (isCompact == compact)
        {
            return;
        }
        isCompact = compact;
        RootGrid.Margin = new Thickness(compact ? 8 : 22);
        HeaderGrid.Margin = new Thickness(0, 0, 0, compact ? 8 : 18);
        AppTitle.FontSize = compact ? 21 : 27;
        NetworkPanel.Padding = new Thickness(compact ? 4 : 14);
        BrushPanel.Padding = new Thickness(compact ? 8 : 14);
        FooterGrid.Margin = new Thickness(2, compact ? 6 : 14, 2, 0);
        Subtitle.IsVisible = !compact;
        BodyGrid.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "280,*");
        BodyGrid.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*");
        Grid.SetColumn(EditorGrid, compact ? 0 : 1);
        Grid.SetRow(EditorGrid, compact ? 1 : 0);
        NetworkExpander.IsExpanded = !compact;
        NetworkScroll.MaxHeight = compact ? 260 : double.PositiveInfinity;
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

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
    private void OnClearClicked(object? sender, RoutedEventArgs eventArgs) => Model?.ClearCanvas();

    private void OnColorChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (Model is not null && BrushColors.SelectedItem is ComboBoxItem item && item.Tag is string value)
        {
            Model.BrushColor = Color.Parse(value).ToUInt32();
        }
    }
}
