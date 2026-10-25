using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using P2PDraw.App.ViewModels;

namespace P2PDraw.App.Views;

public partial class MainWindow : Window
{
    private bool isClosing;
    private bool cleanupComplete;

    public MainWindow()
    {
        InitializeComponent();
        DrawingSurface.StrokeStarted += point => Model?.BeginStroke(point);
        DrawingSurface.StrokeContinued += point => Model?.AppendStroke(point);
        DrawingSurface.StrokeFinished += () => Model?.EndStroke();
        Closing += OnClosing;
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

    private void OnDisconnectClicked(object? sender, RoutedEventArgs eventArgs) => Model?.Disconnect();
    private void OnClearClicked(object? sender, RoutedEventArgs eventArgs) => Model?.ClearCanvas();

    private void OnColorChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (Model is not null && BrushColors.SelectedItem is ComboBoxItem item && item.Tag is string value)
        {
            Model.BrushColor = Color.Parse(value).ToUInt32();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (cleanupComplete)
        {
            return;
        }
        eventArgs.Cancel = true;
        if (isClosing)
        {
            return;
        }
        isClosing = true;
        try
        {
            if (Model is not null)
            {
                await Model.DisposeAsync();
            }
        }
        finally
        {
            cleanupComplete = true;
            Close();
        }
    }
}
