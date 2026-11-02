using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using P2PDraw.App.Views;
using P2PDraw.App.ViewModels;

namespace P2PDraw.App;

public partial class App : Application
{
    public MainWindowViewModel? MobileModel { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(desktop.Args) };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = CreateMobileView;
        }
        base.OnFrameworkInitializationCompleted();
    }

    public MainView CreateMobileView()
    {
        // Activity recreation replaces controls but retains the document and TCP peer.
        MobileModel ??= new MainWindowViewModel();
        MobileModel.LayerEditor.SelectedObjectId = null;
        return new MainView { DataContext = MobileModel };
    }

    public async Task CloseMobileEditorAsync()
    {
        var model = MobileModel;
        MobileModel = null;
        if (model is not null)
        {
            await model.DisposeAsync();
        }
    }
}
