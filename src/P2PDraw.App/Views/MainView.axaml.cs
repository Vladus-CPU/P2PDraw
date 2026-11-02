using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using P2PDraw.App.Controls;
using P2PDraw.App.Documents;
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
        DrawingSurface.ShapeResized += (shapeId, start, end) => Model?.ResizeShape(shapeId, start, end);
        DrawingSurface.SelectionChanged += selected =>
        {
            DeleteButton.IsEnabled = selected is not null;
            DuplicateButton.IsEnabled = selected is not null;
            if (Model is not null)
            {
                Model.LayerEditor.SelectedObjectId = selected;
            }
        };
        LayerSide.CloseRequested += () => LayerSide.IsVisible = false;
        LayerSide.ClearRequested += () => ConfirmLayerAction(false);
        LayerSide.RemoveRequested += () => ConfirmLayerAction(true);
        SizeChanged += OnSizeChanged;
        BodyGrid.SizeChanged += (_, eventArgs) =>
        {
            NetworkPanel.MaxHeight = eventArgs.NewSize.Height;
            LayerSide.MaxHeight = eventArgs.NewSize.Height;
            FilesPanel.MaxHeight = eventArgs.NewSize.Height;
            FileScroll.MaxHeight = Math.Max(0, eventArgs.NewSize.Height - 24);
            LayerSide.Width = isCompact == true ? Math.Min(300, eventArgs.NewSize.Width) : 240;
            NetworkScroll.MaxHeight = Math.Max(0, Math.Min(440, eventArgs.NewSize.Height - 96));
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        bool compact = eventArgs.NewSize.Width < 1000 || eventArgs.NewSize.Height < 600;
        if (isCompact == compact)
        {
            return;
        }
        isCompact = compact;
        LayerSide.IsVisible = !compact;
        Grid.SetColumn(LayerSide, compact ? 0 : 1);
        LayerSide.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        LayerSide.Width = compact ? 300 : 240;
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

    private void OnDuplicateClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DrawingSurface.SelectedObjectId is Guid selected)
        {
            Model?.DuplicateObject(selected);
        }
    }

    private void OnUndoClicked(object? sender, RoutedEventArgs eventArgs) => Model?.Undo();
    private void OnRedoClicked(object? sender, RoutedEventArgs eventArgs) => Model?.Redo();

    private void OnSessionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        FilesPanel.IsVisible = false;
        NetworkPanel.IsVisible = !NetworkPanel.IsVisible;
        NetworkExpander.IsExpanded = NetworkPanel.IsVisible;
        if (isCompact == true && NetworkPanel.IsVisible)
        {
            LayerSide.IsVisible = false;
        }
    }

    private void OnLayersClicked(object? sender, RoutedEventArgs eventArgs)
    {
        FilesPanel.IsVisible = false;
        LayerSide.IsVisible = !LayerSide.IsVisible;
        NetworkPanel.IsVisible = false;
    }

    private void OnFilesClicked(object? sender, RoutedEventArgs eventArgs)
    {
        FilesPanel.IsVisible = !FilesPanel.IsVisible;
        NetworkPanel.IsVisible = false;
        if (isCompact == true)
        {
            LayerSide.IsVisible = false;
        }
    }

    private async void OnExportSvgClicked(object? sender, RoutedEventArgs eventArgs) => await ExportAsync(false);
    private async void OnExportPngClicked(object? sender, RoutedEventArgs eventArgs) => await ExportAsync(true);

    private async Task ExportAsync(bool png)
    {
        var model = Model;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (model is null || storage is null)
        {
            return;
        }
        var snapshot = model.GetSnapshot();
        var included = VisibleContentOnly.IsChecked == true ? model.LayerEditor.VisibleLayerIds.ToHashSet() : null;
        FilesPanel.IsVisible = false;
        try
        {
            bool saved = png
                ? await AppFilesService.SavePngAsync(storage, snapshot, included)
                : await AppFilesService.SaveSvgAsync(storage, snapshot, included);
            if (saved)
            {
                model.Session.Status = png ? "PNG збережено." : "SVG збережено.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (NotSupportedException)
        {
            model.Session.Status = "Системне збереження файлів недоступне.";
        }
        catch (ArgumentException)
        {
            model.Session.Status = "Перевірте координати й розмір документа. PNG підтримує до 4096 px на сторону.";
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException)
        {
            model.Session.Status = "Не вдалося зберегти файл. Виберіть інше місце збереження.";
        }
    }

    private void ConfirmLayerAction(bool remove)
    {
        var model = Model;
        if (model is null || !model.LayerEditor.HasActiveLayer)
        {
            return;
        }
        Guid layerId = model.LayerEditor.ActiveLayerId;
        string verb = remove ? "Видалити" : "Очистити";
        ConfirmAction($"{verb} шар «{model.LayerEditor.LayerName}»? Зміни побачать усі учасники сесії.", () =>
        {
            model.EndStroke();
            if (remove)
            {
                model.LayerEditor.RemoveLayer(layerId);
            }
            else
            {
                model.LayerEditor.ClearLayer(layerId);
            }
        });
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
