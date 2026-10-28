using Android.Runtime;
using Avalonia.Android;

namespace P2PDraw.Android;

[global::Android.App.Application]
public class AndroidApp : AvaloniaAndroidApplication<P2PDraw.App.App>
{
    public AndroidApp(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }
}
