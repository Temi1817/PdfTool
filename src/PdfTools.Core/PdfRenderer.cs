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
                DrawStamps(model, page, rendered);
                return FitIntoBox(rendered, width, height);
            }
        }
        catch (Exception ex) when (ex is not PdfToolException)
        {
            throw new PdfToolException(PdfErrorKind.Corrupted,
                $"Не удалось отрисовать страницу {page.PageIndex + 1}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Рендер страницы целиком (без полей) в JPEG или PNG с заданным DPI, вместе с подписями.
    /// Для сохранения страниц картинками и для окна установки подписи.
    /// </summary>
    public static byte[] RenderPageImage(PdfDocumentModel model, PageRef page, int dpi, bool jpeg)
    {
        var source = model.GetSource(page.SourceId);
        SKBitmap bitmap;
        lock (RenderLock)
        {
            bitmap = Conversion.ToImage(source.Data, page: page.PageIndex, options: new RenderOptions(
                Dpi: dpi, WithAnnotations: true, Rotation: ToPdfRotation(page.Rotation),
                BackgroundColor: SKColors.White));
        }
        using (bitmap)
        {
            DrawStamps(model, page, bitmap);
            using var data = bitmap.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
    }

    /// <summary>Накладывает подписи на уже отрисованную (видимую, повёрнутую) страницу.</summary>
    private static void DrawStamps(PdfDocumentModel model, PageRef page, SKBitmap bitmap)
    {
        if (page.Stamps.Count == 0) return;
        var rotation = model.VisibleRotation(page);
        using var canvas = new SKCanvas(bitmap);
        foreach (var stamp in page.Stamps)
        {
            using var image = SKImage.FromEncodedData(model.GetStampImage(stamp.ImageId));
            if (image == null) continue;

            var r = StampGeometry.ToVisible(stamp.Rect, rotation);
            var angle = PdfOutput.NormalizeRotation(stamp.Rotation + rotation); // как картинка видна
            var (x, y, w, h) = StampGeometry.Fit(r.X * bitmap.Width, r.Y * bitmap.Height,
                r.Width * bitmap.Width, r.Height * bitmap.Height, image.Width, image.Height, angle);

            var sideways = angle is 90 or 270;
            var (drawW, drawH) = sideways ? (h, w) : (w, h);
            canvas.Save();
            canvas.Translate((float)(x + w / 2), (float)(y + h / 2));
            if (angle != 0) canvas.RotateDegrees(angle);
            canvas.DrawImage(image, SKRect.Create((float)(-drawW / 2), (float)(-drawH / 2), (float)drawW, (float)drawH),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.Restore();
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
