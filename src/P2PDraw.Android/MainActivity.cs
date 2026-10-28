using Android.App;
using Android.Content.PM;
using Android.Content;
using Android.Net.Wifi;
using Avalonia.Android;

namespace P2PDraw.Android;

[Activity(Label = "P2PDraw", Theme = "@style/P2PDrawTheme", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private WifiManager.MulticastLock? multicastLock;
    private P2PDraw.App.App? EditorApp => Avalonia.Application.Current as P2PDraw.App.App;

    protected override void OnStart()
    {
        base.OnStart();
        try
        {
            var wifi = ApplicationContext?.GetSystemService(Context.WifiService) as WifiManager;
            multicastLock ??= wifi?.CreateMulticastLock("P2PDraw.discovery");
            multicastLock?.SetReferenceCounted(false);
            multicastLock?.Acquire();
        }
        catch (Java.Lang.SecurityException exception)
        {
            global::Android.Util.Log.Warn("P2PDraw", $"Multicast unavailable: {exception.Message}");
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        EditorApp?.MobileModel?.ResumeNetworking();
    }

    protected override void OnPause()
    {
        EditorApp?.MobileModel?.EndStroke();
        base.OnPause();
    }

    protected override void OnStop()
    {
        if (multicastLock?.IsHeld == true)
        {
            multicastLock.Release();
        }
        base.OnStop();
    }

    protected override async void OnDestroy()
    {
        bool finishing = IsFinishing;
        var editorApp = EditorApp;
        base.OnDestroy();
        multicastLock?.Dispose();
        multicastLock = null;
        if (finishing && editorApp is not null)
        {
            await editorApp.CloseMobileEditorAsync();
        }
    }
}
