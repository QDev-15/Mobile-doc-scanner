using Android.App;
using Android.Gms.Extensions;
using Xamarin.Google.Android.Play.Core.AppUpdate;
using Xamarin.Google.Android.Play.Core.AppUpdate.Install.Model;

namespace DocScanner.Services;

/// <summary>
/// Forces every user onto the latest Play-published build: whenever Play reports a newer version exists, blocks
/// the app behind Play's own full-screen update UI (AppUpdateType.Immediate) until they update or quit. No
/// server of our own - "is there a newer version" and the download/install itself are entirely Play's own
/// backend (the exact one `dotnet publish ... -p:AndroidPackageFormat=aab` uploads a release to). Every release
/// is treated as mandatory; there is no "optional" tier (no priority bookkeeping to maintain anywhere).
///
/// Called from MainActivity.OnResume - Google's own recommended hook, specifically so an Immediate update that
/// was interrupted (the user left mid-download) resumes the moment the app is back in the foreground. A side
/// effect: once a new version is published, EVERY return to MainActivity (including from the gallery/camera/PDF
/// pickers, which are themselves started with StartActivityForResult) re-checks and, if still not updated,
/// re-blocks with the Immediate screen - intentional for "bắt buộc", not a bug.
/// </summary>
internal static class AndroidAppUpdate
{
	public static async void CheckAndForce(Activity activity)
	{
		try
		{
			IAppUpdateManager manager = AppUpdateManagerFactory.Create(activity);
			AppUpdateInfo info = await manager.GetAppUpdateInfo().AsAsync<AppUpdateInfo>();
			int availability = info.UpdateAvailability();

			// Already underway (e.g. the user backed out mid-download last time): resume it, same call as
			// starting fresh - Play itself knows to continue rather than restart the download.
			bool resuming = availability == UpdateAvailability.DeveloperTriggeredUpdateInProgress;
			bool starting = availability == UpdateAvailability.UpdateAvailable && info.IsUpdateTypeAllowed(AppUpdateType.Immediate);
			if (!resuming && !starting) return;

			const int requestCode = 0x5047;
			manager.StartUpdateFlowForResult(info, activity,
				AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), requestCode);
		}
		catch (Exception ex)
		{
			// No Play Services (a sideloaded install outside Play, or a device without it), or a transient
			// Play error: never let this block or crash the app - the user just does not get asked this time;
			// the next OnResume tries again.
			Core.Perf.Log($"update check failed: {ex.Message}");
		}
	}
}
