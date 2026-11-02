using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task GlobalClearRequiresConfirmationAndCanBeCancelled()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.BeginStroke(new(10, 20));
            model.EndStroke();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var view = window.Editor;
            var clear = view.FindControl<Button>("ClearButton")!;
            Assert.Equal("Очистити полотно", clear.Content);

            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(view.FindControl<Border>("ConfirmationOverlay")!.IsVisible);
            Assert.Contains("усі учасники", view.FindControl<TextBlock>("ConfirmationText")!.Text);
            Assert.False(view.FindControl<Grid>("BodyGrid")!.IsEnabled);
            Assert.Single(model.Strokes);
            Assert.Equal(3, model.OperationCount);
            view.FindControl<Button>("CancelActionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(model.Strokes);
            Assert.False(view.FindControl<Border>("ConfirmationOverlay")!.IsVisible);

            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.FindControl<Button>("ConfirmActionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(model.Strokes);
            Assert.Single(model.GetSnapshot().Tombstones);
            Assert.Equal(4, model.OperationCount);
            Assert.True(view.FindControl<Grid>("BodyGrid")!.IsEnabled);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
