using SkiaSharp;

namespace PdfTools.Core;

/// <summary>
/// Подготовка картинки подписи или печати: поворот по EXIF, уменьшение,
/// по желанию — белый (бумажный) фон становится прозрачным, пустые поля обрезаются.
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
