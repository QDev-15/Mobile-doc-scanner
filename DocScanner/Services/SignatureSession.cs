using DocScanner.Core;

namespace DocScanner.Services;

/// <summary>Hands the page being signed (and its picture as the result screen shows it, without signatures) to the
/// signature screen.</summary>
public sealed class SignatureSession
{
	public string? DocId { get; private set; }
	public string? PageId { get; private set; }
	public PreviewFrame? Frame { get; private set; }

	public void Begin(string docId, string pageId, PreviewFrame frame)
	{
		DocId = docId;
		PageId = pageId;
		Frame = frame;
	}

	public void End() => Frame = null;
}
