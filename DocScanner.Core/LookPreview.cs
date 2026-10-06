using ImageCoreService;

namespace DocScanner.Core;

/// <summary>A picture for the result screen: a color page, or a gray / black-and-white one.</summary>
public sealed record PreviewFrame(RgbImage? Color, GrayImage? Gray);

/// <summary>
/// The result screen's live preview as a pipeline whose stages are kept once computed, so each change redoes only
/// what it affects (the way photo editors keep their intermediate buffers):
///
///   straightened page (color) -> gray -> background flattened -> sharpened -> Sauvola statistics -> black and white
///
/// - color: the page itself, nothing to compute; brightness / contrast are a GPU color matrix in the view;
/// - gray: the gray (or flattened) stage, computed once;
/// - black and white: darkness (Sauvola k) and brightness only change the final threshold, one comparison per pixel,
///   because the window mean / deviation are kept (<see cref="Binarizer.Stats"/>): the sliders follow the finger;
/// - a quarter turn turns every stage already computed (a copy each; the statistics of a turned page are exactly the
///   turned statistics) instead of computing them again.
/// <see cref="Warm"/> computes the stages the other looks need in the background, so switching look is immediate too.
/// Same filters and parameters as the saved page (<see cref="DocumentFilter"/>), at screen size. Thread-safe.
/// </summary>
public sealed class LookPreview
{
    private readonly object _lock = new();
    private GrayImage? _gray, _flat, _sharpGray, _sharpFlat;
    private SauvolaStats? _grayStats, _flatStats; // of the sharpened stage the threshold looks at

    public LookPreview(RgbImage straightened) : this(straightened, null, null, null, null, null, null) { }

    private LookPreview(RgbImage page, GrayImage? gray, GrayImage? flat, GrayImage? sharpGray, GrayImage? sharpFlat,
        SauvolaStats? grayStats, SauvolaStats? flatStats)
    {
        Page = page;
        Dpi = CropRenderService.PageDpi(page.Width, page.Height);
        _gray = gray;
        _flat = flat;
        _sharpGray = sharpGray;
        _sharpFlat = sharpFlat;
        _grayStats = grayStats;
        _flatStats = flatStats;
    }

    /// <summary>The straightened, unfiltered page.</summary>
    public RgbImage Page { get; }

    /// <summary>Resolution the filters are sized for (window, speck size), as for the saved page.</summary>
    public int Dpi { get; }

    /// <summary>The page in <paramref name="look"/>, without brightness / contrast for color and gray (the view applies
    /// those live), with the brightness for black and white (it moves the threshold).</summary>
    public PreviewFrame Render(FilterOptions look)
    {
        switch (look.Mode)
        {
            case PageColorMode.Color:
                return new PreviewFrame(Page, null);
            case PageColorMode.Gray:
                return new PreviewFrame(null, Source(look.CleanBackground));
            default:
            {
                if (look.Method == BinarizationMethod.Otsu)
                    return new PreviewFrame(null, DocumentFilter.Apply(Page, look, Dpi).Gray!); // rare; not worth a cached path
                GrayImage source = ThresholdSource(look);
                SauvolaStats stats = Stats(look);
                var bw = new GrayImage(source.Width, source.Height);
                Binarizer.Threshold(source, stats, DocumentFilter.SauvolaKFor(look.Darkness), look.Tone.BrightnessLevels, bw,
                    look.Smooth ? DocumentFilter.SmoothRamp : 0);
                if (look.Despeckle) DocumentFilter.Despeckle(bw, Dpi);
                return new PreviewFrame(null, bw);
            }
        }
    }

    /// <summary>Computes the stages the looks the user may pick next need (gray, flattened, statistics), so that
    /// switching is instant. Cheap to call again: what exists is kept.</summary>
    public void Warm(bool cleanBackground)
    {
        Source(cleanBackground);
        Stats(new FilterOptions(PageColorMode.BlackWhite, CleanBackground: cleanBackground));
    }

    /// <summary>This preview turned by quarter turns: every stage computed so far is turned with it, nothing is recomputed.</summary>
    public LookPreview RotateClockwise(int turns)
    {
        if (((turns % 4) + 4) % 4 == 0) return this;
        lock (_lock)
        {
            return new LookPreview(Page.RotateClockwise(turns), _gray?.RotateClockwise(turns), _flat?.RotateClockwise(turns),
                _sharpGray?.RotateClockwise(turns), _sharpFlat?.RotateClockwise(turns),
                _grayStats?.RotateClockwise(turns), _flatStats?.RotateClockwise(turns));
        }
    }

    private GrayImage Source(bool clean)
    {
        lock (_lock)
        {
            _gray ??= Page.ToGray();
            if (!clean) return _gray;
            return _flat ??= BackgroundFlattener.Flatten(_gray);
        }
    }

    /// <summary>What the black-and-white threshold looks at: the gray / flattened stage, sharpened as for the saved page
    /// (<see cref="DocumentFilter.BlackWhiteSource"/>). Every look here sharpens (the option only exists for tests).</summary>
    private GrayImage ThresholdSource(FilterOptions look)
    {
        GrayImage source = Source(look.CleanBackground);
        if (!look.Sharpen) return source;
        lock (_lock)
        {
            ref GrayImage? slot = ref look.CleanBackground ? ref _sharpFlat : ref _sharpGray;
            if (slot == null)
            {
                var copy = new GrayImage(source.Width, source.Height, (byte[])source.Data.Clone());
                Sharpen.UnsharpInPlace(copy, Sharpen.RadiusFor(Dpi), Sharpen.DefaultAmount);
                slot = copy;
            }
            return slot;
        }
    }

    private SauvolaStats Stats(FilterOptions look)
    {
        GrayImage source = ThresholdSource(look);
        lock (_lock)
        {
            if (look.CleanBackground) return _flatStats ??= Binarizer.Stats(source, Binarizer.DefaultWindow(Dpi));
            return _grayStats ??= Binarizer.Stats(source, Binarizer.DefaultWindow(Dpi));
        }
    }
}
