namespace DocScanner.Services;

/// <summary>Runtime permission helper. Only the camera needs one: gallery import uses the
/// system photo picker (no storage permission on any supported Android version).</summary>
public sealed class PermissionService
{
	public async Task<bool> EnsureCameraAsync()
	{
		PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.Camera>();
		if (status == PermissionStatus.Granted) return true;

		// Denied earlier: Android shows a rationale first, and after a permanent denial it
		// will not show the system dialog again at all.
		if (Permissions.ShouldShowRationale<Permissions.Camera>())
		{
			await Shell.Current.DisplayAlertAsync("Cần quyền camera",
				"Doc Scanner cần camera để chụp tài liệu. Ảnh chỉ lưu trên máy của bạn.", "OK");
		}

		status = await Permissions.RequestAsync<Permissions.Camera>();
		if (status == PermissionStatus.Granted) return true;

		bool openSettings = await Shell.Current.DisplayAlertAsync("Chưa có quyền camera",
			"Hãy cấp quyền Camera trong Cài đặt của ứng dụng để chụp tài liệu.", "Mở Cài đặt", "Để sau");
		if (openSettings) AppInfo.ShowSettingsUI();
		return false;
	}
}
