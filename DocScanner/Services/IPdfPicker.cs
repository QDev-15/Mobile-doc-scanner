using DocScanner.Core;

namespace DocScanner.Services;

/// <summary>Picks one PDF file and rasterizes every page of it into an importable page source.</summary>
public interface IPdfPicker
{
	/// <summary>One <see cref="ImportSource"/> per page, in page order; empty when the user backed out or the
	/// file could not be read as a PDF.</summary>
	Task<IReadOnlyList<ImportSource>> PickAsync();
}
