using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfTools.Core;

/// <param name="PageCount">Сколько страниц обработано.</param>
/// <param name="PagesAsImages">Сколько страниц без текста (сканы) вставлено картинками.</param>
public sealed record DocxConversionResult(int PageCount, int PagesAsImages);

/// <summary>
/// PDF → Word (.docx) своими силами, без Microsoft Word.
/// Текст становится редактируемыми абзацами (размер, жирность, цвет),
/// картинки переносятся, страницы-сканы вставляются картинками целиком.
/// Сложная вёрстка (колонки, текст поверх картинок) упрощается до потока абзацев.
/// </summary>
public static class PdfToDocxConverter
{
    private const int ScanDpi = 150;

    /// <summary>Конвертирует текущий вид документа (с поворотами, удалениями, перестановками).</summary>
    public static DocxConversionResult Convert(PdfDocumentModel model, string docxPath)
    {
        ArgumentNullException.ThrowIfNull(model);

        byte[] pdfBytes;
        using (var output = new PdfOutput(model.GetSource))
            pdfBytes = output.WriteToBytes(model.Pages);

        var result = new DocxConversionResult(0, 0);
        PdfOutput.WriteAtomically(docxPath, stream =>
        {
            using var writer = new DocxWriter(stream);
            result = WriteDocument(pdfBytes, writer);
        });
        return result;
    }

    private static DocxConversionResult WriteDocument(byte[] pdfBytes, DocxWriter writer)
    {
        PigDocument pdf;
        try
        {
            pdf = PigDocument.Open(pdfBytes);
        }
        catch (Exception ex)
        {
            throw new PdfToolException(PdfErrorKind.ConversionFailed,
                $"Не удалось разобрать PDF для конвертации:\n{ex.Message}", ex);
        }

        using (pdf)
        {
            var pagesAsImages = 0;
            for (var n = 1; n <= pdf.NumberOfPages; n++)
            {
                if (n > 1) writer.AddPageBreak();
                try
                {
                    if (!WritePage(pdf.GetPage(n), writer))
                    {
                        // На странице нет текста — вставляем её целиком картинкой
                        if (InsertPageAsImage(pdfBytes, n - 1, writer)) pagesAsImages++;
                    }
                }
                catch (Exception ex) when (ex is not PdfToolException)
                {
                    // Страницу не удалось разобрать — хотя бы картинкой, остальные продолжаем
                    if (InsertPageAsImage(pdfBytes, n - 1, writer)) pagesAsImages++;
                }
            }
            return new DocxConversionResult(pdf.NumberOfPages, pagesAsImages);
        }
    }

    /// <summary>Пишет текст и картинки страницы. false — на странице нет текста.</summary>
    private static bool WritePage(Page page, DocxWriter writer)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        if (words.Count == 0) return false;

        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList();

        // Картинки сверху вниз; каждую вставляем перед первым блоком текста, который ниже неё
        var images = page.GetImages().OrderByDescending(i => i.BoundingBox.Top).ToList();
        var nextImage = 0;

        foreach (var block in ordered)
        {
            while (nextImage < images.Count && images[nextImage].BoundingBox.Top >= block.BoundingBox.Top)
                AddImage(images[nextImage++], writer);
            WriteBlock(block, writer);
        }
        while (nextImage < images.Count)
            AddImage(images[nextImage++], writer);
        return true;
    }

    private sealed record TextStyle(double Size, bool Bold, string? Color)
    {
        public bool SameAs(TextStyle other) =>
            Math.Abs(Size - other.Size) <= Math.Max(Size, other.Size) * 0.15
            && Bold == other.Bold && Color == other.Color;
    }

    /// <summary>
    /// Блок может содержать и заголовок, и текст под ним — делим его на абзацы
    /// там, где меняется стиль строк или между строками большой отступ.
    /// </summary>
    private static void WriteBlock(TextBlock block, DocxWriter writer)
    {
        var group = new List<TextLine>();
        TextStyle? groupStyle = null;
        TextLine? previous = null;

        foreach (var line in block.TextLines)
        {
            var letters = line.Words.SelectMany(w => w.Letters).ToList();
            if (letters.Count == 0) continue;
            var style = StyleOf(letters);

            var bigGap = previous != null &&
                         previous.BoundingBox.Bottom - line.BoundingBox.Top > 1.5 * previous.BoundingBox.Height;
            if (groupStyle != null && (!groupStyle.SameAs(style) || bigGap))
            {
                Flush(group, groupStyle, writer);
                groupStyle = null;
            }

            groupStyle ??= style;
            group.Add(line);
            previous = line;
        }
        if (groupStyle != null) Flush(group, groupStyle, writer);
    }

    private static void Flush(List<TextLine> lines, TextStyle style, DocxWriter writer)
    {
        writer.AddParagraph(JoinLines(lines.Select(l => l.Text)), style.Size, style.Bold, style.Color);
        lines.Clear();
    }

    private static TextStyle StyleOf(IReadOnlyList<Letter> letters)
    {
        var sizes = letters.Select(l => l.PointSize).Where(s => s > 0).Order().ToList();
        var size = sizes.Count > 0 ? sizes[sizes.Count / 2] : 11;
        var bold = letters.Count(IsBold) * 2 > letters.Count;
        return new TextStyle(Math.Round(size, 1), bold, DominantColor(letters));
    }

    /// <summary>Строки одного блока — это переносы внутри абзаца: склеиваем, убирая переносы по слогам.</summary>
    internal static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0)
            {
                var hyphenated = sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && char.IsLower(line[0]);
                if (hyphenated) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static bool IsBold(Letter letter)
    {
        var name = letter.FontName ?? "";
        return name.Contains("Bold", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Самый частый цвет букв в шестнадцатеричном виде; null, если почти чёрный.</summary>
    private static string? DominantColor(IReadOnlyList<Letter> letters)
    {
        var top = letters
            .Select(l =>
            {
                try
                {
                    var (r, g, b) = l.Color.ToRGBValues();
                    return $"{ToByte(r):X2}{ToByte(g):X2}{ToByte(b):X2}";
                }
                catch
                {
                    return "000000";
                }
            })
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .First().Key;

        var dark = System.Convert.ToInt32(top[..2], 16) < 40 && System.Convert.ToInt32(top[2..4], 16) < 40
                   && System.Convert.ToInt32(top[4..], 16) < 40;
        return dark ? null : top;
    }

    private static int ToByte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static void AddImage(IPdfImage image, DocxWriter writer)
    {
        try
        {
            byte[] data;
            bool isJpeg;
            if (image.TryGetPng(out var png))
            {
                data = png;
                isJpeg = false;
            }
            else
            {
                // Без перекодирования PdfPig отдаёт JPEG как есть
                var raw = image.RawBytes.ToArray();
                if (raw.Length < 3 || raw[0] != 0xFF || raw[1] != 0xD8) return;
                data = raw;
                isJpeg = true;
            }
            writer.AddImage(data, isJpeg, image.BoundingBox.Width, image.BoundingBox.Height);
        }
        catch
        {
            // Нестандартный формат картинки — пропускаем, текст важнее
        }
    }

    private static bool InsertPageAsImage(byte[] pdfBytes, int pageIndex, DocxWriter writer)
    {
        byte[] png;
        try
        {
            png = PdfRenderer.RenderPagePng(pdfBytes, pageIndex, ScanDpi);
        }
        catch
        {
            return false;
        }

        // Размер картинки в пунктах = пиксели / DPI * 72
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        if (bmp == null) return false;
        // Полностью белая страница (пустая) — не вставляем
        if (IsBlank(bmp)) return false;
        writer.AddImage(png, false, bmp.Width * 72.0 / ScanDpi, bmp.Height * 72.0 / ScanDpi);
        return true;
    }

    private static bool IsBlank(SkiaSharp.SKBitmap bmp)
    {
        for (var y = 0; y < bmp.Height; y += 8)
        for (var x = 0; x < bmp.Width; x += 8)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red < 245 || c.Green < 245 || c.Blue < 245) return false;
        }
        return true;
    }
}
