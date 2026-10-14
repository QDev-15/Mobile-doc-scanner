using Android.App;
using Android.Content.PM;
using Android.OS;

namespace DocScanner;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		// Every system dialog the app opens (Shell.DisplayAlert / DisplayPrompt / DisplayActionSheet, which MAUI builds from an AppCompat
		// AlertDialog.Builder using this activity's alertDialogTheme) gets the rounded, shadowed card style from
		// Resources/values/styles.xml. Looked up by name so no generated Resource class is needed here.
		int overlay = Resources?.GetIdentifier("AppDialogThemeOverlay", "style", PackageName) ?? 0;
		if (overlay != 0) Theme?.ApplyStyle(overlay, true);
	}

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
