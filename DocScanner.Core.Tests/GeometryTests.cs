using DocScanner.Core;

namespace DocScanner.Core.Tests;

public class GeometryTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void Orientations_5_to_8_swap_width_and_height(int orientation, bool swaps)
    {
        Assert.Equal(swaps, ImageGeometry.IsTransposed(orientation));
        Assert.Equal(swaps ? (3000, 4000) : (4000, 3000), ImageGeometry.UprightSize(4000, 3000, orientation));
    }

    [Fact]
    public void FitLongEdge_keeps_aspect_and_never_upscales()
    {
        Assert.Equal((1600, 1200), ImageGeometry.FitLongEdge(8000, 6000, 1600));
        Assert.Equal((1200, 1600), ImageGeometry.FitLongEdge(6000, 8000, 1600));
        Assert.Equal((800, 600), ImageGeometry.FitLongEdge(800, 600, 1600));
        Assert.Equal((1, 1600), ImageGeometry.FitLongEdge(1, 100000, 1600)); // extreme strip stays >= 1 px
    }

    [Theory]
    [InlineData(8000, 6000, 1600, 4)]   // 48 MP -> 2000 px: never decodes below the proxy size
    [InlineData(4000, 3000, 1600, 2)]   // 12 MP -> 2000 px
    [InlineData(3000, 2000, 1600, 1)]   // 1500 px would be too small, so no shrink
    [InlineData(1000, 800, 1600, 1)]
    public void DecodeSampleSize_leaves_at_least_the_target(int w, int h, int target, int expected)
    {
        int s = ImageGeometry.DecodeSampleSize(w, h, target);
        Assert.Equal(expected, s);
        if (Math.Max(w, h) >= target) Assert.True(Math.Max(w, h) / s >= target);
    }
}
