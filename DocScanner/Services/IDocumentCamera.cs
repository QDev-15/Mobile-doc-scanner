namespace DocScanner.Services;

/// <summary>Photos taken with the in-app document camera (files in the app's cache, in shooting order), or
/// <see cref="Error"/> when the camera could not be started (the caller then falls back to the system camera app).</summary>
public sealed record DocumentCameraResult(IReadOnlyList<string> Photos, string? Error = null);

/// <summary>The in-app document camera: live preview with the sheet outlined as it is detected, automatic capture when
/// the sheet is held still, several pages in one go.</summary>
public interface IDocumentCamera
{
	Task<DocumentCameraResult> ScanAsync();
}
