using DocScanner.Core;

namespace DocScanner.Services;

/// <summary>The system photo picker. Returns as soon as the user has chosen: no photo is read yet (the sources open
/// lazily), so choosing 100 photos costs nothing until the background import copies them.</summary>
public interface IPhotoPicker
{
	/// <summary>The chosen photos, in the picker's order; empty when the user backed out.</summary>
	Task<IReadOnlyList<ImportSource>> PickAsync();
}
