using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace P2PDraw.Android;

[Activity(Label = "P2PDraw", Theme = "@style/P2PDrawTheme", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
