using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace PdfTools.Core;

/// <summary>
/// Картинки (фото с телефона, сканы) → PDF. Каждая картинка — отдельная страница A4,
/// ориентация листа по картинке, картинка вписана с небольшими полями.
/// Поворот фото по EXIF учитывается.
/// </summary>
public static class ImagePdfBuilder
{
    private const double A4Short = 595.28;
    private const double A4Long = 841.89;
    private const double Margin = 18;

    public static readonly IReadOnlyList<string> ImageExtensions =
        new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static void Create(IReadOnlyList<string> imagePaths, string outputPdf)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Не выбрано ни одной картинки.");

        var images = new List<XImage>();
        try
        {
            using var doc = new PdfDocument();
            doc.Info.Creator = "PdfTool";
            foreach (var path in imagePaths)
            {
                var image = XImage.FromStream(new MemoryStream(PrepareImage(path)));
                images.Add(image);

                var landscape = image.PixelWidth > image.PixelHeight;
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(landscape ? A4Long : A4Short);
                page.Height = XUnit.FromPoint(landscape ? A4Short : A4Long);

                var boxW = page.Width.Point - 2 * Margin;
                var boxH = page.Height.Point - 2 * Margin;
                var scale = Math.Min(boxW / image.PixelWidth, boxH / image.PixelHeight);
                var w = image.PixelWidth * scale;
                var h = image.PixelHeight * scale;

                using var g = XGraphics.FromPdfPage(page);
                g.DrawImage(image, (page.Width.Point - w) / 2, (page.Height.Point - h) / 2, w, h);
            }
            PdfOutput.SaveDocument(doc, outputPdf);
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    /// <summary>
    /// Возвращает байты JPEG/PNG, готовые для PDF: если фото повёрнуто по EXIF
    /// или формат не JPEG/PNG — перекодируем, иначе берём файл как есть (без потери качества).
    /// </summary>
    private static byte[] PrepareImage(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw PdfToolException.FromIo(ex, path, writing: false);
        }

        var name = Path.GetFileName(path);
        using var codec = SKCodec.Create(new MemoryStream(bytes))
            ?? throw new PdfToolException(PdfErrorKind.Corrupted,
                $"Файл «{name}» не является картинкой или повреждён.");

        var format = codec.EncodedFormat;
        var origin = codec.EncodedOrigin;
        if (origin == SKEncodedOrigin.TopLeft && format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png)
            return bytes;

        using var decoded = SKBitmap.Decode(codec)
            ?? throw new PdfToolException(PdfErrorKind.Corrupted, $"Не удалось прочитать картинку «{name}».");
        using var oriented = ApplyOrigin(decoded, origin);
        // Скриншоты и рисунки (PNG/GIF/BMP) — без потерь, чтобы мелкий текст не размывался; фото — JPEG
        var lossless = format is SKEncodedImageFormat.Png or SKEncodedImageFormat.Gif or SKEncodedImageFormat.Bmp;
        using var data = oriented.Encode(lossless ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    /// <summary>Разворачивает картинку по EXIF-ориентации (как её показывает телефон).</summary>
    internal static SKBitmap ApplyOrigin(SKBitmap bmp, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? bmp.Height : bmp.Width;
        var h = swap ? bmp.Width : bmp.Height;

        var result = new SKBitmap(new SKImageInfo(w, h, bmp.ColorType, bmp.AlphaType));
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(w, h); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, h); canvas.RotateDegrees(270); break;
        }
        using (var image = SKImage.FromBitmap(bmp))
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return result;
    }
}
