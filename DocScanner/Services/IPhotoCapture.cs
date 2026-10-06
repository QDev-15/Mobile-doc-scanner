namespace DocScanner.Services;

/// <summary>Takes one photo with the system camera app; returns the file (in the app's cache), or null when the user
/// backed out.</summary>
public interface IPhotoCapture
{
	Task<string?> CaptureAsync();
}
