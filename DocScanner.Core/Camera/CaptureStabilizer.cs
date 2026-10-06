using ImageCoreService;

namespace DocScanner.Core.Camera;

/// <summary>What the live document camera should tell the user (and whether to take the picture now).</summary>
public enum CaptureCue
{
    /// <summary>No sheet in view.</summary>
    Searching,
    /// <summary>A sheet is in view but too small to be worth a picture: move closer.</summary>
    TooFar,
    /// <summary>The sheet runs out of the picture (a corner on the frame border): step back.</summary>
    OutOfFrame,
    /// <summary>A sheet is in view and the camera is being held still; <see cref="StabilizerState.Progress"/> fills up.</summary>
    Holding,
    /// <summary>Take the picture now (automatic capture).</summary>
    Capture,
    /// <summary>This sheet was just captured: waiting for the next page (the view changes) before capturing again.</summary>
    Captured,
}

public readonly record struct StabilizerState(CaptureCue Cue, double Progress);

/// <summary>
/// Automatic capture for the live document camera: fires once the detected outline has stayed put for
/// <see cref="HoldSeconds"/>, then waits for the next page before firing again. A new page is recognised when the
/// outline moves away, disappears for a moment (the page is lifted or turned), or the picture itself changes a lot (a
/// new sheet laid down in the same place: <see cref="FrameSignature"/>). Pure logic, fed one analysed frame at a time.
/// </summary>
public sealed class CaptureStabilizer
{
    /// <summary>How long the outline must stay still before the automatic capture.</summary>
    public double HoldSeconds { get; init; } = 0.9;

    /// <summary>How far (fraction of the frame, any corner) the outline may drift and still count as "still".</summary>
    public double StillTolerance { get; init; } = 0.025;

    /// <summary>Smallest outline (fraction of the frame area) worth capturing automatically.</summary>
    public double MinArea { get; init; } = 0.12;

    /// <summary>A corner closer than this (fraction of the frame) to the border counts as cut off by the frame.</summary>
    public double FrameMargin { get; init; } = 0.008;

    /// <summary>Weakest detection trusted for an automatic capture (a manual capture takes anything).</summary>
    public double MinConfidence { get; init; } = 0.5;

    /// <summary>After a capture: how far the outline must move, how long it must be gone, or how much the picture must
    /// change (mean of the <see cref="FrameSignature"/> cells, grey levels), for the next page to be recognised.</summary>
    public double ReleaseMove { get; init; } = 0.08;
    public double ReleaseLostSeconds { get; init; } = 0.5;
    public double ReleaseContentChange { get; init; } = 14;

    private Quad? _anchor;
    private double _anchorSince;
    private bool _locked;
    private Quad? _lockedQuad;
    private double[]? _lockedSignature;
    private double? _lostSince;

    /// <summary>One analysed frame: the outline (normalized 0..1) or null when no sheet was found, the detection
    /// confidence, and the frame's <see cref="FrameSignature"/> (optional).</summary>
    public StabilizerState Update(double timeSeconds, Quad? outline, double confidence, double[]? signature = null)
    {
        if (outline is not { } q)
        {
            _anchor = null;
            _lostSince ??= timeSeconds;
            if (_locked && timeSeconds - _lostSince.Value >= ReleaseLostSeconds) Unlock();
            return new(_locked ? CaptureCue.Captured : CaptureCue.Searching, 0);
        }
        _lostSince = null;

        if (_locked)
        {
            bool moved = _lockedQuad is { } lq && Distance(q, lq) > ReleaseMove;
            bool changed = signature != null && _lockedSignature != null && SignatureDistance(signature, _lockedSignature) > ReleaseContentChange;
            if (!moved && !changed) return new(CaptureCue.Captured, 0);
            Unlock();
        }

        if (q.Area < MinArea || confidence < MinConfidence || TouchesFrame(q))
        {
            _anchor = null;
            CaptureCue cue = TouchesFrame(q) ? CaptureCue.OutOfFrame : q.Area < MinArea ? CaptureCue.TooFar : CaptureCue.Searching;
            return new(cue, 0);
        }

        if (_anchor is not { } a || Distance(q, a) > StillTolerance)
        {
            _anchor = q;
            _anchorSince = timeSeconds;
            return new(CaptureCue.Holding, 0);
        }

        double progress = (timeSeconds - _anchorSince) / HoldSeconds;
        return progress >= 1 ? new(CaptureCue.Capture, 1) : new(CaptureCue.Holding, Math.Max(0, progress));
    }

    /// <summary>A picture was taken (automatically or with the shutter) of <paramref name="outline"/> (null when no sheet
    /// was in view): no automatic capture again until the next page.</summary>
    public void Captured(Quad? outline, double[]? signature)
    {
        _locked = true;
        _lockedQuad = outline;
        _lockedSignature = signature;
        _anchor = null;
    }

    public void Reset()
    {
        Unlock();
        _anchor = null;
        _lostSince = null;
    }

    private void Unlock()
    {
        _locked = false;
        _lockedQuad = null;
        _lockedSignature = null;
    }

    private bool TouchesFrame(Quad q) =>
        q.ToArray().Any(p => p.X <= FrameMargin || p.Y <= FrameMargin || p.X >= 1 - FrameMargin || p.Y >= 1 - FrameMargin);

    /// <summary>Largest corner movement between two outlines (normalized coordinates).</summary>
    public static double Distance(Quad a, Quad b)
    {
        PointD[] p = a.ToArray(), r = b.ToArray();
        double max = 0;
        for (int i = 0; i < 4; i++)
            max = Math.Max(max, Math.Sqrt((p[i].X - r[i].X) * (p[i].X - r[i].X) + (p[i].Y - r[i].Y) * (p[i].Y - r[i].Y)));
        return max;
    }

    /// <summary>A coarse fingerprint of a frame: mean grey level of an 8 x 8 grid.</summary>
    public static double[] FrameSignature(RgbImage image)
    {
        const int n = 8;
        var sum = new double[n * n];
        var count = new int[n * n];
        byte[] d = image.Data;
        int w = image.Width, h = image.Height;
        for (int y = 0; y < h; y += 2)
        {
            int cy = y * n / h;
            for (int x = 0; x < w; x += 2)
            {
                int i = (y * w + x) * 3, c = cy * n + x * n / w;
                sum[c] += (d[i] * 77 + d[i + 1] * 150 + d[i + 2] * 29) >> 8;
                count[c]++;
            }
        }
        for (int c = 0; c < sum.Length; c++) sum[c] = count[c] == 0 ? 0 : sum[c] / count[c];
        return sum;
    }

    public static double SignatureDistance(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }
}
