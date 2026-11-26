using PDFtoImage;
using SkiaSharp;

namespace PdfTools.Core;

/// <summary>
/// Рендер миниатюр страниц через PDFium (PDFtoImage).
/// Результат — PNG фиксированного размера: страница вписана по центру,
/// остальное прозрачное. Так UI не зависит от SkiaSharp, а Core — от WinForms.
/// </summary>
public static class PdfRenderer
{
    // PDFium не потокобезопасен — рендерим строго по одному
    private static readonly object RenderLock = new();

    /// <summary>Рендерит страницу <paramref name="page"/> документа в PNG размером width×height.</summary>
    public static byte[] RenderThumbnailPng(PdfDocumentModel model, PageRef page, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(page);
        if (width < 8 || height < 8)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Слишком маленький размер миниатюры.");

        var source = model.GetSource(page.SourceId);

        try
        {
            SKBitmap rendered;
            lock (RenderLock)
            {
                // Рендерим по DPI — так пропорции страницы сохраняются при любом повороте.
                // DPI берём с запасом по короткой стороне, лишнее уменьшится в FitIntoBox.
                var size = Conversion.GetPageSize(source.Data, page: page.PageIndex);
                var shortSide = Math.Max(1f, Math.Min(size.Width, size.Height));
                var dpi = (int)Math.Ceiling(72f * Math.Max(width, height) / shortSide);

                var options = new RenderOptions(
                    Dpi: Math.Clamp(dpi, 10, 600),
                    WithAnnotations: true,
                    Rotation: ToPdfRotation(page.Rotation),
                    BackgroundColor: SKColors.White);
                rendered = Conversion.ToImage(source.Data, page: page.PageIndex, options: options);
            }
            using (rendered)
            {
                return FitIntoBox(rendered, width, height);
            }
        }
        catch (Exception ex) when (ex is not PdfToolException)
        {
            throw new PdfToolException(PdfErrorKind.Corrupted,
                $"Не удалось отрисовать страницу {page.PageIndex + 1}: {ex.Message}", ex);
        }
    }

    /// <summary>Рендер целой страницы в PNG с заданным DPI (для вставки сканов в Word).</summary>
    internal static byte[] RenderPagePng(byte[] pdfBytes, int pageIndex, int dpi)
    {
        SKBitmap bitmap;
        lock (RenderLock)
        {
            bitmap = Conversion.ToImage(pdfBytes, page: pageIndex,
                options: new RenderOptions(Dpi: dpi, WithAnnotations: true, BackgroundColor: SKColors.White));
        }
        using (bitmap)
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        {
            return data.ToArray();
        }
    }

    private static PdfRotation ToPdfRotation(int degrees) => (((degrees % 360) + 360) % 360) switch
    {
        90 => PdfRotation.Rotate90,
        180 => PdfRotation.Rotate180,
        270 => PdfRotation.Rotate270,
        _ => PdfRotation.Rotate0
    };

    /// <summary>Вписывает картинку в прямоугольник по центру и рисует тонкую рамку вокруг страницы.</summary>
    private static byte[] FitIntoBox(SKBitmap bitmap, int width, int height)
    {
        var scale = Math.Min(1f, Math.Min((float)width / bitmap.Width, (float)height / bitmap.Height));
        var w = Math.Max(1f, bitmap.Width * scale);
        var h = Math.Max(1f, bitmap.Height * scale);
        var rect = SKRect.Create((width - w) / 2f, (height - h) / 2f, w, h);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using (var image = SKImage.FromBitmap(bitmap))
        {
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using (var border = new SKPaint { Color = new SKColor(150, 150, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
        {
            canvas.DrawRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Width - 1, rect.Height - 1, border);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
