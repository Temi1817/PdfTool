using System.Runtime.InteropServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SkiaSharp;

namespace PdfTools.Core;

/// <param name="OriginalBytes">Размер документа без сжатия (как при обычном сохранении).</param>
/// <param name="CompressedBytes">Размер после сжатия.</param>
/// <param name="ImagesRecompressed">Сколько картинок пережато.</param>
/// <param name="Saved">false — уменьшить не удалось, файл не записан.</param>
public sealed record CompressionResult(long OriginalBytes, long CompressedBytes, int ImagesRecompressed, bool Saved);

/// <summary>
/// Сжатие PDF: крупные картинки уменьшаются и пережимаются в JPEG (серые остаются серыми),
/// служебные потоки сжимаются сильнее. Текст и векторная графика не трогаются.
/// </summary>
public static class PdfCompressor
{
    /// <summary>Длинная сторона картинки после сжатия: A4 примерно в 150 DPI.</summary>
    private const int MaxImageSide = 1754;
    private const int JpegQuality = 70;

    public static CompressionResult Compress(PdfDocumentModel model, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var output = new PdfOutput(model.GetSource);

        long original;
        using (var plain = output.Build(model.Pages))
            original = SizeOf(plain);

        using var doc = output.Build(model.Pages);
        var recompressed = RecompressImages(doc);
        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;

        var bytes = ToBytes(doc);
        if (bytes.Length >= original)
            return new CompressionResult(original, bytes.Length, recompressed, Saved: false);

        PdfOutput.WriteAtomically(outputPath, s => s.Write(bytes));
        return new CompressionResult(original, bytes.Length, recompressed, Saved: true);
    }

    private static long SizeOf(PdfDocument doc) => ToBytes(doc).LongLength;

    private static byte[] ToBytes(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static int RecompressImages(PdfDocument doc)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var count = 0;
        foreach (var page in doc.Pages)
            count += Visit(page.Elements.GetDictionary("/Resources"), seen, 0);
        return count;
    }

    // Обходим картинки страницы, включая вложенные формы; общие картинки — один раз
    private static int Visit(PdfDictionary? resources, HashSet<PdfDictionary> seen, int depth)
    {
        var xobjects = resources?.Elements.GetDictionary("/XObject");
        if (xobjects == null || depth > 8) return 0;

        var count = 0;
        foreach (var key in xobjects.Elements.Keys.ToList())
        {
            if (xobjects.Elements.GetDictionary(key) is not { } xo || !seen.Add(xo)) continue;
            var subtype = xo.Elements.GetName("/Subtype");
            if (subtype == "/Image" && TryRecompress(xo)) count++;
            else if (subtype == "/Form") count += Visit(xo.Elements.GetDictionary("/Resources"), seen, depth + 1);
        }
        return count;
    }

    private static bool TryRecompress(PdfDictionary image)
    {
        try
        {
            var e = image.Elements;
            // Прозрачность, маски и нестандартные цвета не трогаем — JPEG их не передаст
            if (e.ContainsKey("/SMask") || e.ContainsKey("/Mask") || e.ContainsKey("/Decode")
                || e.GetBoolean("/ImageMask") || e.GetInteger("/BitsPerComponent") != 8 || image.Stream == null)
                return false;

            var raw = image.Stream.Value;
            var filter = FilterName(e);
            var components = ComponentCount(e);
            SKBitmap? bitmap = filter switch
            {
                "/DCTDecode" => SKBitmap.Decode(raw),
                "/FlateDecode" when components is 1 or 3 && !HasPredictor(e) =>
                    FromPixels(image.Stream.UnfilteredValue, e.GetInteger("/Width"), e.GetInteger("/Height"), components),
                _ => null
            };
            if (bitmap == null) return false;

            using (bitmap)
            {
                var gray = components == 1;
                var encoded = Encode(bitmap, gray);
                if (encoded.Length >= raw.Length) return false; // не стало меньше — оставляем как было

                image.Stream.Value = encoded;
                e.SetName("/Filter", "/DCTDecode");
                e.Remove("/DecodeParms");
                e.SetName("/ColorSpace", gray ? "/DeviceGray" : "/DeviceRGB");
                e.SetInteger("/BitsPerComponent", 8);
                using var check = SKBitmap.Decode(encoded);
                e.SetInteger("/Width", check.Width);
                e.SetInteger("/Height", check.Height);
                return true;
            }
        }
        catch
        {
            return false; // необычная картинка — пропускаем, документ важнее
        }
    }

    private static byte[] Encode(SKBitmap bitmap, bool gray)
    {
        var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(bitmap.Width, bitmap.Height));
        var w = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var h = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

        using var target = new SKBitmap(new SKImageInfo(w, h, gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, SKRect.Create(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        using var data = target.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data.ToArray();
    }

    private static SKBitmap? FromPixels(byte[] pixels, int width, int height, int components)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * components) return null;

        var rgba = new byte[width * height * 4];
        for (int i = 0, j = 0; i < width * height; i++, j += components)
        {
            rgba[i * 4] = pixels[j];
            rgba[i * 4 + 1] = components == 3 ? pixels[j + 1] : pixels[j];
            rgba[i * 4 + 2] = components == 3 ? pixels[j + 2] : pixels[j];
            rgba[i * 4 + 3] = 255;
        }
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        return bitmap;
    }

    private static string? FilterName(PdfDictionary.DictionaryElements e)
    {
        var filter = e["/Filter"];
        if (filter is PdfReference r) filter = r.Value;
        return filter switch
        {
            PdfName name => name.Value,
            PdfArray { Elements.Count: 1 } array => (array.Elements[0] as PdfName)?.Value,
            _ => null
        };
    }

    /// <summary>1 — серая, 3 — RGB (в том числе ICCBased с N=1/3); 0 — другое (CMYK, палитра …).</summary>
    private static int ComponentCount(PdfDictionary.DictionaryElements e)
    {
        var cs = e["/ColorSpace"];
        if (cs is PdfReference r) cs = r.Value;
        switch (cs)
        {
            case PdfName { Value: "/DeviceGray" }: return 1;
            case PdfName { Value: "/DeviceRGB" }: return 3;
            case PdfArray { Elements.Count: 2 } array when (array.Elements[0] as PdfName)?.Value == "/ICCBased":
                var profile = array.Elements[1] is PdfReference pr ? pr.Value as PdfDictionary : array.Elements[1] as PdfDictionary;
                var n = profile?.Elements.GetInteger("/N") ?? 0;
                return n is 1 or 3 ? n : 0;
            default: return 0;
        }
    }

    private static bool HasPredictor(PdfDictionary.DictionaryElements e) =>
        e.GetDictionary("/DecodeParms")?.Elements.GetInteger("/Predictor") > 1;
}
