namespace Printman.Services.Renderers;

/// <summary>Bounds work and decoded page buffers before rendering or spool submission.</summary>
public static class DocumentRenderLimits
{
    public const int MaxPages = 1000;
    public const int MaxDimension = 20000;
    public const long MaxPixels = 40_000_000;

    public static void ValidatePageCount(long pages)
    {
        if (pages is < 1 or > MaxPages)
            throw new InvalidDataException($"Document must contain between 1 and {MaxPages} pages.");
    }

    public static void ValidateDimensions(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
            width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
            throw new InvalidDataException("Decoded page exceeds the supported dimensions or pixel limit.");
    }
}
