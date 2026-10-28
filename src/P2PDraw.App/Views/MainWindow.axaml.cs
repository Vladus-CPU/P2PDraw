using Avalonia.Controls;
using P2PDraw.App.ViewModels;

namespace P2PDraw.App.Views;

public partial class MainWindow : Window
{
    private bool isClosing;
    private bool cleanupComplete;
    public MainView Editor => EditorView;
    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
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
