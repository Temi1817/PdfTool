using SkiaSharp;

namespace PdfTools.Core;

public enum CropShape
{
    Rectangle,
    /// <summary>Овал, вписанный в прямоугольник, — для круглой печати.</summary>
    Ellipse
}

/// <summary>
/// Подготовка картинки подписи или печати: поворот по EXIF, уменьшение,
/// по желанию — белый (бумажный) фон становится прозрачным, пустые поля обрезаются.
/// Плюс ручная правка: обрезка рамкой/овалом и ластик.
/// </summary>
public static class StampImage
{
    /// <summary>Длинная сторона после уменьшения: для подписи 4–5 см это ~600 DPI.</summary>
    private const int MaxSide = 1200;

    public static byte[] Prepare(byte[] fileBytes, bool removeBackground)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        using var codec = SKCodec.Create(new MemoryStream(fileBytes))
            ?? throw new PdfToolException(PdfErrorKind.Corrupted, "Файл не является картинкой или повреждён.");
        using var decoded = SKBitmap.Decode(codec)
            ?? throw new PdfToolException(PdfErrorKind.Corrupted, "Не удалось прочитать картинку.");
        using var oriented = ImagePdfBuilder.ApplyOrigin(decoded, codec.EncodedOrigin);
        using var bitmap = ToRgba(oriented, MaxSide);

        if (removeBackground) MakePaperTransparent(bitmap);

        using var trimmed = Trim(bitmap) ?? throw new PdfToolException(PdfErrorKind.InvalidInput,
            "На картинке не найдено подписи — она пустая или слишком светлая.\nПопробуйте без «Убрать белый фон».");
        using var data = trimmed.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ───────────── Ручная правка: обрезка и ластик ─────────────

    /// <summary>
    /// Оставляет только выделенную область (прямоугольник или вписанный в него овал — для круглой печати),
    /// всё снаружи становится прозрачным. Координаты — в пикселях картинки.
    /// </summary>
    public static byte[] Crop(byte[] png, int x, int y, int width, int height, CropShape shape)
    {
        using var bmp = DecodeRgba(png);
        var left = Math.Clamp(x, 0, bmp.Width);
        var top = Math.Clamp(y, 0, bmp.Height);
        var right = Math.Clamp(x + width, 0, bmp.Width);
        var bottom = Math.Clamp(y + height, 0, bmp.Height);
        if (right - left < 3 || bottom - top < 3)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Выделите область побольше.");

        double cx = (left + right) / 2.0, cy = (top + bottom) / 2.0;
        double rx = (right - left) / 2.0, ry = (bottom - top) / 2.0;
        var pixels = bmp.Pixels;
        for (var py = 0; py < bmp.Height; py++)
        for (var px = 0; px < bmp.Width; px++)
        {
            var inside = px >= left && px < right && py >= top && py < bottom;
            if (inside && shape == CropShape.Ellipse)
            {
                double dx = (px + 0.5 - cx) / rx, dy = (py + 0.5 - cy) / ry;
                inside = dx * dx + dy * dy <= 1;
            }
            if (!inside) pixels[py * bmp.Width + px] = SKColors.Transparent;
        }
        bmp.Pixels = pixels;
        return Finish(bmp);
    }

    /// <summary>Стирает след ластика: линия по точкам (в пикселях картинки) толщиной 2×radius.</summary>
    public static byte[] Erase(byte[] png, IReadOnlyList<(float X, float Y)> stroke, float radius)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        using var bmp = DecodeRgba(png);
        if (stroke.Count == 0 || radius <= 0) return png;

        var pixels = bmp.Pixels;
        void Dot(float cx, float cy)
        {
            var r2 = radius * radius;
            for (var py = Math.Max(0, (int)(cy - radius)); py <= Math.Min(bmp.Height - 1, (int)(cy + radius)); py++)
            for (var px = Math.Max(0, (int)(cx - radius)); px <= Math.Min(bmp.Width - 1, (int)(cx + radius)); px++)
            {
                float dx = px + 0.5f - cx, dy = py + 0.5f - cy;
                if (dx * dx + dy * dy <= r2) pixels[py * bmp.Width + px] = SKColors.Transparent;
            }
        }

        Dot(stroke[0].X, stroke[0].Y);
        for (var i = 1; i < stroke.Count; i++)
        {
            // Точки мыши идут с пропусками — заполняем отрезок между ними
            var (x0, y0) = stroke[i - 1];
            var (x1, y1) = stroke[i];
            var steps = Math.Max(1, (int)(MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / Math.Max(1, radius / 2)));
            for (var s = 1; s <= steps; s++)
                Dot(x0 + (x1 - x0) * s / steps, y0 + (y1 - y0) * s / steps);
        }
        bmp.Pixels = pixels;
        return Finish(bmp);
    }

    private static SKBitmap DecodeRgba(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var decoded = SKBitmap.Decode(png)
            ?? throw new PdfToolException(PdfErrorKind.Corrupted, "Не удалось прочитать картинку подписи.");
        return ToRgba(decoded, int.MaxValue);
    }

    private static byte[] Finish(SKBitmap bmp)
    {
        using var trimmed = Trim(bmp) ?? throw new PdfToolException(PdfErrorKind.InvalidInput,
            "После этого от картинки ничего не останется. Выделите другую область.");
        using var data = trimmed.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Копия в RGBA (без premultiply — удобно менять прозрачность), уменьшенная до maxSide.</summary>
    private static SKBitmap ToRgba(SKBitmap source, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, SKRect.Create(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return result;
    }

    /// <summary>
    /// Бумага → прозрачная. Цвет бумаги оцениваем по самым светлым пикселям (90-й процентиль яркости),
    /// поэтому сероватая бумага на фото тоже уходит. Чернила (синие, чёрные) остаются.
    /// </summary>
    private static void MakePaperTransparent(SKBitmap bmp)
    {
        var pixels = bmp.Pixels;
        var whiteness = new int[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            whiteness[i] = Math.Min(c.Red, Math.Min(c.Green, c.Blue)); // у цветных чернил минимум низкий
        }

        // Только непрозрачные пиксели: у готовой PNG-подписи на прозрачном фоне бумаги нет
        var sorted = whiteness.Where((_, i) => pixels[i].Alpha > 200).ToArray();
        if (sorted.Length == 0) return;
        Array.Sort(sorted);
        var paper = sorted[(int)(sorted.Length * 0.9)];
        var hi = Math.Max(60, paper - 25);   // светлее — полностью прозрачно
        var lo = Math.Max(0, hi - 90);       // темнее — полностью видно

        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            var opacity = Math.Clamp((hi - whiteness[i]) / (double)(hi - lo), 0, 1);
            var alpha = (byte)Math.Round(c.Alpha * opacity);
            pixels[i] = c.WithAlpha(alpha);
        }
        bmp.Pixels = pixels;
    }

    /// <summary>Обрезает прозрачные поля (оставляя небольшой отступ). null — картинка пустая.</summary>
    private static SKBitmap? Trim(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            if (bmp.GetPixel(x, y).Alpha <= 24) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) return null;

        var pad = Math.Max(2, Math.Max(bmp.Width, bmp.Height) / 100);
        var rect = new SKRectI(Math.Max(0, minX - pad), Math.Max(0, minY - pad),
            Math.Min(bmp.Width, maxX + pad + 1), Math.Min(bmp.Height, maxY + pad + 1));

        // ExtractSubset ссылается на пиксели исходной картинки — делаем независимую копию
        using var subset = new SKBitmap();
        return bmp.ExtractSubset(subset, rect) ? subset.Copy() : null;
    }
}
