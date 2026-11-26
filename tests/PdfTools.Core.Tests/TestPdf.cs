using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfTools.Core.Tests;

/// <summary>
/// Генерация тестовых PDF. Страницы различаются шириной (в пунктах),
/// по ней проверяем, что порядок страниц после операций правильный.
/// </summary>
internal static class TestPdf
{
    public static string Create(string dir, string name, params double[] widths)
    {
        var path = Path.Combine(dir, name);
        using var doc = new PdfDocument();
        foreach (var w in widths)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(w);
            page.Height = XUnit.FromPoint(800);
        }
        doc.Save(path);
        return path;
    }

    /// <summary>Создаёт PDF из N страниц шириной 101, 102, 103 …</summary>
    public static string CreateNumbered(string dir, string name, int count) =>
        Create(dir, name, Enumerable.Range(1, count).Select(i => 100.0 + i).ToArray());

    /// <summary>
    /// PDF со страницами разного содержимого: "text", "blank", "image", "text+image",
    /// "heading" (жирный красный 30pt «Заголовок» + обычный текст ниже), "word:XXX" (текст XXX).
    /// </summary>
    public static string CreateMixed(string dir, string name, params string[] kinds)
    {
        EnsureFonts();
        var imagePath = Path.Combine(dir, $"img{Guid.NewGuid():N}.png");
        using (var bmp = new SkiaSharp.SKBitmap(200, 100))
        {
            bmp.Erase(SkiaSharp.SKColors.SteelBlue);
            using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(imagePath, data.ToArray());
        }

        var path = Path.Combine(dir, name);
        using (var doc = new PdfDocument())
        {
            foreach (var kind in kinds)
            {
                var page = doc.AddPage();
                if (kind == "blank") continue;
                using var g = XGraphics.FromPdfPage(page);
                if (kind == "heading")
                {
                    g.DrawString("Заголовок", new XFont("Arial", 30, XFontStyleEx.Bold), XBrushes.Red, 50, 100);
                    g.DrawString("Обычный текст", new XFont("Arial", 12), XBrushes.Black, 50, 300);
                    continue;
                }
                if (kind.StartsWith("word:"))
                {
                    g.DrawString(kind[5..], new XFont("Arial", 14), XBrushes.Black, 50, 100);
                    continue;
                }
                if (kind.Contains("image"))
                {
                    using var img = XImage.FromFile(imagePath);
                    g.DrawImage(img, 50, 50, 400, 200);
                }
                if (kind.Contains("text"))
                    g.DrawString("Привет, текст", new XFont("Arial", 20), XBrushes.Black, 50, 400);
            }
            doc.Save(path);
        }
        File.Delete(imagePath);
        return path;
    }

    private static readonly object FontLock = new();
    private static bool _fontsReady;

    private static void EnsureFonts()
    {
        lock (FontLock)
        {
            if (_fontsReady) return;
            PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            _fontsReady = true;
        }
    }

    public static double[] ReadWidths(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => Math.Round(p.MediaBox.Width)).ToArray();
    }

    public static int[] ReadRotations(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => p.Rotate).ToArray();
    }
}

/// <summary>Временная папка, удаляется после теста.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PdfToolTests_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* не мешаем тестам */ }
    }
}
