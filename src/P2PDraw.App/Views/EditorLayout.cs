using System.ComponentModel;
using Avalonia;
using Avalonia.Layout;

namespace P2PDraw.App.Views;

public sealed class EditorLayout : INotifyPropertyChanged
{
    private Size size;
    private double bodyWidth;
    private double fontSize;
    private bool? wideLayout;
    private bool hasSize;
    private bool hasBodyWidth;
    private bool hasSelection;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string? ActivePanel { get; private set; } = "Шари";
    public bool IsWide => wideLayout == true;
    public bool IsNarrow => hasSize && size.Width < 600;
    public bool IsShort => hasSize && size.Height < 600;
    public bool InlineProperties => !hasSize || size.Width >= 1100 && !IsShort && fontSize <= 16;
    public bool ShowBrand => !IsNarrow;
    public bool ShowDetails => !hasSize || size.Width >= 850;
    public bool ShowSelection => hasSelection && ShowDetails;
    public bool ShowPanel => ActivePanel is not null;
    public bool ShowBackdrop => ShowPanel && !IsWide;
    public bool ShowLayers => ActivePanel == "Шари";
    public bool ShowSession => ActivePanel == "Сесія";
    public bool ShowFiles => ActivePanel == "Файл";
    public bool ShowProperties => ActivePanel == "Властивості";
    public bool ShowEditing => ActivePanel == "Редагування";
    public bool ShowPanelHeader => !ShowLayers;
    public bool ShowToolbarAndFooter => !ShowPanel || !hasSize || size.Height >= 320;
    public int PanelColumn => IsWide ? 1 : 0;
    public int PanelColumnSpan => IsWide ? 1 : 2;
    public double PanelWidth => IsWide || !hasBodyWidth ? 280 : Math.Max(0, Math.Min(360, bodyWidth - 8));
    public Thickness PanelMargin => new(IsWide ? 8 : 0, 0, 0, 0);
    public Thickness RootMargin { get; private set; } = new(12);
    public double RowSpacing => IsShort ? 2 : 6;
    public Thickness ToolbarPadding { get; private set; } = new(6);
    public Orientation PropertiesOrientation => InlineProperties ? Orientation.Horizontal : Orientation.Vertical;
    public double ConfirmationMaxHeight => hasSize ? Math.Max(0, size.Height - 72) : double.PositiveInfinity;

    public void Update(Size size, double fontSize)
    {
        if (hasSize && this.size == size && this.fontSize == fontSize)
        {
            return;
        }
        this.size = size;
        this.fontSize = fontSize;
        hasSize = true;
        RootMargin = new Thickness(IsShort ? 4 : 8);
        ToolbarPadding = new Thickness(IsShort ? 2 : 4);
        bool wide = size.Width >= 1000 && !IsShort;
        if (wideLayout != wide)
        {
            // A temporary panel must not appear just because the window became smaller.
            ActivePanel = wide ? "Шари" : null;
            wideLayout = wide;
        }
        if (ActivePanel == "Властивості" && InlineProperties || ActivePanel == "Редагування" && !IsNarrow)
        {
            ActivePanel = null;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void UpdateBodyWidth(double width)
    {
        if (hasBodyWidth && bodyWidth == width)
        {
            return;
        }
        bodyWidth = width;
        hasBodyWidth = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PanelWidth)));
    }

    public void SetSelection(bool selected)
    {
        if (hasSelection == selected)
        {
            return;
        }
        hasSelection = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSelection)));
    }

    public void Toggle(string name)
    {
        ActivePanel = ActivePanel == name ? null : name;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void Close()
    {
        if (ActivePanel is null)
        {
            return;
        }
        ActivePanel = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
