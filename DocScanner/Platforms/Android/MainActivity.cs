using Android.App;
using Android.Content.PM;
using Android.OS;

namespace DocScanner;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	/// <summary>Google's own recommended hook for in-app updates: re-checks every time the app comes back to the
	/// foreground, so an Immediate update interrupted last time (the user left mid-download) resumes.</summary>
	protected override void OnResume()
	{
		base.OnResume();
		Services.AndroidAppUpdate.CheckAndForce(this);
	}

	protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent? data)
	{
		if (!Services.AndroidPhotoPicker.OnActivityResult(requestCode, resultCode, data)
		    && !Services.AndroidPdfPicker.OnActivityResult(requestCode, resultCode, data)
		    && !Services.AndroidPhotoCapture.OnActivityResult(requestCode, resultCode)
		    && !Services.AndroidDocumentCamera.OnActivityResult(requestCode, resultCode, data))
			base.OnActivityResult(requestCode, resultCode, data);
	}
}
