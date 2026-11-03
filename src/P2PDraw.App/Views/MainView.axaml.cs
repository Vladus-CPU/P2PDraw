using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using System.ComponentModel;
using Avalonia.VisualTree;
using P2PDraw.App.Controls;
using P2PDraw.App.Documents;
using P2PDraw.App.ViewModels;
using P2PDraw.Core.Models;
using P2PDraw.Core.Storage;

namespace P2PDraw.App.Views;

public partial class MainView : UserControl
{
    private bool? wideLayout;
    private string? activePanel = "Шари";
    private TopLevel? topLevel;
    private MainWindowViewModel? observedModel;
    private Func<Task>? confirmedAction;
    private bool restoreControlsAfterLayout;

    public MainView()
    {
        InitializeComponent();
        UpdateToolProperties();
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
                var shape = Model.Shapes.FirstOrDefault(item => item.ShapeId == selected);
                string name = shape?.Kind switch
                {
                    ShapeKind.Line => "лінія",
                    ShapeKind.Rectangle => "прямокутник",
                    ShapeKind.Ellipse => "еліпс",
                    _ => "штрих"
                };
                SelectionInfo.Text = selected is null ? "" : $"Вибрано: {name}";
                SelectionInfo.IsVisible = selected is not null && Bounds.Width >= 850;
            }
            UpdateToolProperties();
        };
        LayerSide.CloseRequested += ClosePanel;
        LayerSide.ClearRequested += () => ConfirmLayerAction(false);
        LayerSide.RemoveRequested += () => ConfirmLayerAction(true);
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) =>
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            ExportSvgButton.IsEnabled = storage?.CanSave == true;
            ExportPngButton.IsEnabled = storage?.CanSave == true;
            SaveProjectButton.IsEnabled = storage?.CanSave == true;
            OpenProjectButton.IsEnabled = storage?.CanOpen == true;
        };
        BodyGrid.SizeChanged += (_, _) => UpdatePanelLayout();
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        UpdateLayoutMode(eventArgs.NewSize);
    }

    private void UpdateLayoutMode(Size size)
    {
        bool shortScreen = size.Height < 600;
        bool wide = size.Width >= 1000 && !shortScreen;
        bool narrow = size.Width < 600;
        bool inlineProperties = size.Width >= 1100 && !shortScreen && FontSize <= 16;
        RootGrid.Margin = new Thickness(shortScreen ? 4 : 8);
        RootGrid.RowSpacing = shortScreen ? 2 : 6;
        BrushPanel.Padding = new Thickness(shortScreen ? 2 : 4);
        BrandPanel.IsVisible = !narrow;
        ConnectionStatusText.IsVisible = size.Width >= 850;
        PropertiesButton.IsVisible = !inlineProperties;
        MoreToolsButton.IsVisible = narrow;
        EditSeparator.IsVisible = !narrow;
        PropertiesSeparator.IsVisible = inlineProperties;
        SelectionInfo.IsVisible = DrawingSurface.SelectedObjectId is not null && size.Width >= 850;

        if (narrow && EditToolsGroup.Parent == ToolbarGrid)
        {
            ToolbarGrid.Children.Remove(EditToolsGroup);
            EditingHost.Children.Add(EditToolsGroup);
        }
        else if (!narrow && EditToolsGroup.Parent == EditingHost)
        {
            EditingHost.Children.Remove(EditToolsGroup);
            ToolbarGrid.Children.Add(EditToolsGroup);
        }
        if (!inlineProperties && PropertiesGroup.Parent == ToolbarGrid)
        {
            ToolbarGrid.Children.Remove(PropertiesGroup);
            PropertiesHost.Children.Add(PropertiesGroup);
        }
        else if (inlineProperties && PropertiesGroup.Parent == PropertiesHost)
        {
            PropertiesHost.Children.Remove(PropertiesGroup);
            ToolbarGrid.Children.Add(PropertiesGroup);
        }
        PropertiesGroup.Orientation = inlineProperties ? Orientation.Horizontal : Orientation.Vertical;
        if (wideLayout != wide)
        {
            // A temporary panel must not appear just because the window became smaller.
            activePanel = wide ? "Шари" : null;
            wideLayout = wide;
        }
        if (activePanel == "Властивості" && inlineProperties || activePanel == "Редагування" && !narrow)
        {
            activePanel = null;
        }
        UpdatePanelLayout();
        restoreControlsAfterLayout = true;
    }

    private void OnLayoutUpdated(object? sender, EventArgs eventArgs)
    {
        if (!restoreControlsAfterLayout)
        {
            return;
        }
        restoreControlsAfterLayout = false;
        foreach (var button in new[] { SelectTool, BrushTool, LineTool, RectangleTool, EllipseTool })
        {
            if (button.Classes.Contains("selected"))
            {
                button.BringIntoView();
                break;
            }
        }
        if (topLevel?.FocusManager?.GetFocusedElement() is TextBox field && this.IsVisualAncestorOf(field))
        {
            field.BringIntoView();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs eventArgs)
    {
        base.OnPropertyChanged(eventArgs);
        if (eventArgs.Property == FontSizeProperty && RootGrid is not null)
        {
            UpdateLayoutMode(Bounds.Size);
        }
    }

    private void UpdatePanelLayout()
    {
        bool wide = wideLayout == true;
        bool open = activePanel is not null;
        SidePanel.IsVisible = open;
        PanelBackdrop.IsVisible = open && !wide;
        Grid.SetColumn(SidePanel, wide ? 1 : 0);
        Grid.SetColumnSpan(SidePanel, wide ? 1 : 2);
        SidePanel.HorizontalAlignment = HorizontalAlignment.Right;
        SidePanel.Width = wide ? 280 : Math.Max(0, Math.Min(360, BodyGrid.Bounds.Width - 8));
        SidePanel.Margin = new Thickness(wide ? 8 : 0, 0, 0, 0);
        PanelTitle.Text = activePanel;
        LayerSide.IsVisible = activePanel == "Шари";
        NetworkPanel.IsVisible = activePanel == "Сесія";
        FilesPanel.IsVisible = activePanel == "Файл";
        PanelHeader.IsVisible = !LayerSide.IsVisible;
        PropertiesPanel.IsVisible = activePanel == "Властивості";
        EditingPanel.IsVisible = activePanel == "Редагування";
        FilesButton.Classes.Set("selected", FilesPanel.IsVisible);
        LayersButton.Classes.Set("selected", LayerSide.IsVisible);
        SessionButton.Classes.Set("selected", NetworkPanel.IsVisible);
        bool reducedViewport = open && Bounds.Height < 320;
        BrushPanel.IsVisible = !reducedViewport;
        FooterGrid.IsVisible = !reducedViewport;
        ConfirmationScroll.MaxHeight = Math.Max(0, Bounds.Height - 72);
    }

    private void TogglePanel(string name)
    {
        LayerSide.CloseMenu();
        activePanel = activePanel == name ? null : name;
        UpdatePanelLayout();
    }

    private void ClosePanel()
    {
        LayerSide.CloseMenu();
        activePanel = null;
        UpdatePanelLayout();
    }

    public bool TryClosePanel()
    {
        if (ConfirmationOverlay.IsVisible)
        {
            CloseConfirmation();
            return true;
        }
        if (LayerSide.IsVisible && LayerSide.CloseMenu())
        {
            return true;
        }
        if (activePanel is null)
        {
            return false;
        }
        ClosePanel();
        return true;
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
        UpdateToolProperties();
    }

    private void UpdateToolProperties()
    {
        bool drawing = DrawingSurface.Tool != DrawingTool.Select || DrawingSurface.SelectedObjectId is not null;
        WidthSettings.IsVisible = drawing;
        FillSetting.IsVisible = DrawingSurface.Tool == DrawingTool.Rectangle || DrawingSurface.Tool == DrawingTool.Ellipse;
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
        TogglePanel("Сесія");
    }

    private void OnLayersClicked(object? sender, RoutedEventArgs eventArgs)
    {
        TogglePanel("Шари");
    }

    private void OnFilesClicked(object? sender, RoutedEventArgs eventArgs)
    {
        TogglePanel("Файл");
    }

    private void OnPropertiesClicked(object? sender, RoutedEventArgs eventArgs) => TogglePanel("Властивості");
    private void OnMoreToolsClicked(object? sender, RoutedEventArgs eventArgs) => TogglePanel("Редагування");
    private void OnClosePanelClicked(object? sender, RoutedEventArgs eventArgs) => ClosePanel();

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
        ClosePanel();
        var files = new EditorFileActions(model);
        await files.ExportAsync(storage, png, snapshot, included);
    }

    private async void OnSaveProjectClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var model = Model;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (model is null || storage is null)
        {
            return;
        }
        ClosePanel();
        var files = new EditorFileActions(model);
        await files.SaveProjectAsync(storage);
    }

    private async void OnOpenProjectClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var model = Model;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (model is null || storage is null)
        {
            return;
        }
        ClosePanel();
        var files = new EditorFileActions(model);
        var file = await files.ChooseProjectAsync(storage);
        if (file is not null)
        {
            ConfirmAsyncAction($"Відкрити «{file.Name}» як нову сесію? Поточне підключення закриється. Збережіть поточний документ перед продовженням.",
                () => OpenConfirmedProjectAsync(file));
        }
    }

    private async Task OpenConfirmedProjectAsync(ProjectFile file)
    {
        var model = Model;
        if (model is null)
        {
            return;
        }
        SetEditorEnabled(false);
        try
        {
            var files = new EditorFileActions(model);
            if (await files.OpenProjectAsync(file))
            {
                DrawingSurface.ClearSelection();
                DrawingSurface.ResetView();
                model.Session.Status = $"«{file.Name}» відкрито як нову сесію.";
            }
        }
        finally
        {
            SetEditorEnabled(true);
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
                ClosePanel();
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
            if (Model.ConnectedPeers > 0)
            {
                ClosePanel();
            }
        }
    }

    private async void OnJoinManualClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (Model is not null)
        {
            await Model.JoinManualSessionAsync();
            if (Model.ConnectedPeers > 0)
            {
                ClosePanel();
            }
        }
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs eventArgs) => DrawingSurface.ResetView();
    private void OnZoomInClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var center = new Point(DrawingSurface.Bounds.Width / 2, DrawingSurface.Bounds.Height / 2);
        DrawingSurface.ZoomAt(1.2, center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var center = new Point(DrawingSurface.Bounds.Width / 2, DrawingSurface.Bounds.Height / 2);
        DrawingSurface.ZoomAt(1 / 1.2, center);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = TryClosePanel();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            topLevel.BackRequested += OnBackRequested;
        }
        ObserveModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        if (topLevel is not null)
        {
            topLevel.BackRequested -= OnBackRequested;
            topLevel = null;
        }
        if (observedModel is not null)
        {
            observedModel.PropertyChanged -= OnModelChanged;
            observedModel = null;
        }
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs eventArgs) => eventArgs.Handled = TryClosePanel();

    private void ObserveModel()
    {
        if (observedModel is not null)
        {
            observedModel.PropertyChanged -= OnModelChanged;
        }
        observedModel = Model;
        if (observedModel is not null && topLevel is not null)
        {
            observedModel.PropertyChanged += OnModelChanged;
        }
        UpdateConnectionIndicator();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.ConnectedPeers))
        {
            UpdateConnectionIndicator();
        }
    }

    private void UpdateConnectionIndicator()
    {
        if (ConnectionIndicator is not null)
        {
            ConnectionIndicator.Background = Model?.ConnectedPeers > 0 ? Brushes.SeaGreen : Brushes.SlateGray;
        }
    }

    protected override void OnDataContextChanged(EventArgs eventArgs)
    {
        base.OnDataContextChanged(eventArgs);
        ObserveModel();
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
        ConfirmAsyncAction(message, () =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    private void ConfirmAsyncAction(string message, Func<Task> action)
    {
        confirmedAction = action;
        ConfirmationText.Text = message;
        ConfirmationOverlay.IsVisible = true;
        SetEditorEnabled(false);
        CancelActionButton.Focus();
    }

    private void OnCancelActionClicked(object? sender, RoutedEventArgs eventArgs) => CloseConfirmation();

    private async void OnConfirmActionClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var action = confirmedAction;
        CloseConfirmation();
        if (action is not null)
        {
            await action();
        }
    }

    private void CloseConfirmation()
    {
        confirmedAction = null;
        ConfirmationOverlay.IsVisible = false;
        SetEditorEnabled(true);
    }

    private void SetEditorEnabled(bool enabled)
    {
        HeaderGrid.IsEnabled = enabled;
        BrushPanel.IsEnabled = enabled;
        BodyGrid.IsEnabled = enabled;
        FooterGrid.IsEnabled = enabled;
    }

    private void OnColorChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (Model is not null && BrushColors.SelectedItem is ComboBoxItem item && item.Tag is string value)
        {
            Model.BrushColor = Color.Parse(value).ToUInt32();
        }
    }
}
