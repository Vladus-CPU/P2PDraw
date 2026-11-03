using Avalonia;
using Avalonia.Layout;
using P2PDraw.App.Views;
using Xunit;

namespace P2PDraw.Tests.App;

public class EditorLayoutStateTests
{
    [Fact]
    public void InitialLayoutKeepsDefaultsUntilFirstSizeIsKnown()
    {
        var layout = new EditorLayout();

        Assert.True(layout.ShowLayers);
        Assert.True(layout.ShowBrand);
        Assert.True(layout.InlineProperties);
        Assert.True(layout.ShowToolbarAndFooter);
        Assert.Equal(new Thickness(12), layout.RootMargin);
        Assert.Equal(new Thickness(6), layout.ToolbarPadding);
        Assert.Equal(280, layout.PanelWidth);

        layout.Update(new Size(393, 851), 14);

        Assert.Null(layout.ActivePanel);
        Assert.False(layout.ShowBackdrop);
        Assert.True(layout.IsNarrow);
        Assert.False(layout.InlineProperties);
    }

    [Theory]
    [InlineData(599, 600, 14, true, false, false)]
    [InlineData(600, 600, 14, false, false, false)]
    [InlineData(999, 600, 14, false, false, false)]
    [InlineData(1000, 600, 14, false, true, false)]
    [InlineData(1099, 600, 14, false, true, false)]
    [InlineData(1100, 600, 16, false, true, true)]
    [InlineData(1100, 600, 17, false, true, false)]
    [InlineData(1100, 599, 14, false, false, false)]
    public void ModeThresholdsKeepExistingToolbarAndPanelRules(int width, int height, double fontSize, bool narrow, bool wide, bool inline)
    {
        var layout = new EditorLayout();

        layout.Update(new Size(width, height), fontSize);

        Assert.Equal(narrow, layout.IsNarrow);
        Assert.Equal(wide, layout.IsWide);
        Assert.Equal(inline, layout.InlineProperties);
        Assert.Equal(inline ? Orientation.Horizontal : Orientation.Vertical, layout.PropertiesOrientation);
        Assert.Equal(new Thickness(height < 600 ? 4 : 8), layout.RootMargin);
        Assert.Equal(new Thickness(height < 600 ? 2 : 4), layout.ToolbarPadding);
        Assert.Equal(wide, layout.ShowLayers);
        Assert.Equal(wide ? 1 : 0, layout.PanelColumn);
        Assert.Equal(wide ? 1 : 2, layout.PanelColumnSpan);
    }

    [Fact]
    public void PanelSwitchingKeepsOnlyRequestedContentVisible()
    {
        var layout = new EditorLayout();
        layout.Update(new Size(800, 600), 14);

        layout.Toggle("Шари");
        Assert.True(layout.ShowLayers);
        Assert.True(layout.ShowBackdrop);
        Assert.False(layout.ShowPanelHeader);

        layout.Toggle("Сесія");
        Assert.True(layout.ShowSession);
        Assert.False(layout.ShowLayers);
        Assert.True(layout.ShowPanelHeader);

        layout.Toggle("Файл");
        Assert.True(layout.ShowFiles);
        Assert.False(layout.ShowSession);

        layout.Toggle("Файл");
        Assert.Null(layout.ActivePanel);
        Assert.False(layout.ShowPanel);
        Assert.False(layout.ShowBackdrop);
    }

    [Fact]
    public void OnlyDockModeChangesReplaceTheOpenPanel()
    {
        var layout = new EditorLayout();
        layout.Update(new Size(1280, 800), 14);
        layout.Toggle("Файл");

        layout.Update(new Size(1200, 750), 14);
        Assert.True(layout.ShowFiles);

        layout.Update(new Size(900, 750), 14);
        Assert.Null(layout.ActivePanel);

        layout.Toggle("Сесія");
        layout.Update(new Size(800, 700), 14);
        Assert.True(layout.ShowSession);

        layout.Update(new Size(1100, 700), 14);
        Assert.True(layout.ShowLayers);
        Assert.False(layout.ShowBackdrop);

        layout.Update(new Size(1100, 599), 14);
        Assert.Null(layout.ActivePanel);
    }

    [Fact]
    public void ToolbarPanelsCloseWhenTheirControlsReturnInline()
    {
        var layout = new EditorLayout();
        layout.Update(new Size(1024, 768), 14);
        layout.Toggle("Властивості");

        layout.Update(new Size(1100, 768), 14);
        Assert.Null(layout.ActivePanel);

        layout.Update(new Size(1100, 768), 20);
        layout.Toggle("Властивості");
        layout.Update(new Size(1100, 768), 14);
        Assert.Null(layout.ActivePanel);

        layout.Update(new Size(393, 851), 14);
        layout.Toggle("Редагування");
        layout.Update(new Size(600, 851), 14);
        Assert.Null(layout.ActivePanel);
    }

    [Fact]
    public void BodyWidthOnlyChangesTheAvailablePanelWidth()
    {
        var layout = new EditorLayout();
        layout.Update(new Size(800, 600), 14);
        layout.Toggle("Файл");
        var notifications = new List<string?>();
        layout.PropertyChanged += (_, eventArgs) => notifications.Add(eventArgs.PropertyName);

        layout.UpdateBodyWidth(200);
        Assert.Equal(192, layout.PanelWidth);
        layout.UpdateBodyWidth(1000);
        Assert.Equal(360, layout.PanelWidth);
        layout.UpdateBodyWidth(0);
        Assert.Equal(0, layout.PanelWidth);

        Assert.True(layout.ShowFiles);
        Assert.False(layout.IsWide);
        Assert.Equal(new[] { nameof(EditorLayout.PanelWidth), nameof(EditorLayout.PanelWidth), nameof(EditorLayout.PanelWidth) }, notifications);

        layout.Update(new Size(1100, 600), 14);
        Assert.Equal(280, layout.PanelWidth);
    }

    [Fact]
    public void ReducedHeightHidesToolbarOnlyWhilePanelIsOpen()
    {
        var layout = new EditorLayout();
        layout.Update(new Size(844, 280), 14);
        Assert.True(layout.ShowToolbarAndFooter);

        layout.Toggle("Шари");
        Assert.False(layout.ShowToolbarAndFooter);
        Assert.Equal(208, layout.ConfirmationMaxHeight);

        layout.Close();
        Assert.True(layout.ShowToolbarAndFooter);

        layout.Toggle("Шари");
        layout.Update(new Size(844, 320), 14);
        Assert.True(layout.ShowToolbarAndFooter);
        Assert.True(layout.ShowLayers);
    }

    [Fact]
    public void SelectionDetailsFollowWidthWithoutOpeningPanels()
    {
        var layout = new EditorLayout();
        layout.Update(new Size(849, 600), 14);
        layout.SetSelection(true);
        Assert.False(layout.ShowSelection);
        Assert.False(layout.ShowDetails);

        layout.Update(new Size(850, 600), 14);
        Assert.True(layout.ShowSelection);
        Assert.True(layout.ShowDetails);
        Assert.Null(layout.ActivePanel);

        layout.SetSelection(false);
        Assert.False(layout.ShowSelection);
    }

    [Fact]
    public void UnchangedInputsDoNotRestartLayoutNotifications()
    {
        var layout = new EditorLayout();
        var size = new Size(800, 600);
        layout.Update(size, 14);
        layout.UpdateBodyWidth(784);
        var notifications = new List<string?>();
        layout.PropertyChanged += (_, eventArgs) => notifications.Add(eventArgs.PropertyName);

        layout.Update(size, 14);
        layout.UpdateBodyWidth(784);
        layout.SetSelection(false);
        layout.Close();
        Assert.Empty(notifications);

        layout.Toggle("Файл");
        layout.Update(size, 14);
        layout.Close();
        layout.Close();
        layout.UpdateBodyWidth(780);
        layout.SetSelection(true);

        Assert.Equal(new string?[] { null, null, nameof(EditorLayout.PanelWidth), nameof(EditorLayout.ShowSelection) }, notifications);
    }
}
