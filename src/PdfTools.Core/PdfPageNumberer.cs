using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PdfTools.Core;

public enum NumberPosition
{
    BottomCenter,
    BottomRight,
    BottomLeft,
    TopCenter,
    TopRight
}

/// <param name="Format">Шаблон: {n} — номер, {total} — всего страниц. Например "Стр. {n} из {total}".</param>
/// <param name="StartFrom">С какого числа начинать нумерацию.</param>
public sealed record PageNumberOptions(
    NumberPosition Position = NumberPosition.BottomCenter,
    string Format = "{n}",
    int StartFrom = 1,
    double FontSize = 11);

/// <summary>Проставляет номера страниц и сохраняет результат в новый файл.</summary>
public static class PdfPageNumberer
{
    private const double Margin = 24;
    private static readonly object FontLock = new();
    private static bool _fontsReady;

    public static void AddNumbers(PdfDocumentModel model, string outputPath, PageNumberOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Format.Contains("{n}"))
            throw new PdfToolException(PdfErrorKind.InvalidInput, "В формате номера должно быть {n}.");
        if (options.StartFrom is < 0 or > 100_000)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Начальный номер должен быть от 0 до 100000.");

        EnsureFonts();
        using var output = new PdfOutput(model.GetSource);
        using var doc = output.Build(model.Pages);
        var font = new XFont("Arial", options.FontSize);
        var total = doc.PageCount + options.StartFrom - 1;

        for (var i = 0; i < doc.PageCount; i++)
        {
            var text = options.Format
                .Replace("{n}", (options.StartFrom + i).ToString())
                .Replace("{total}", total.ToString());
            DrawNumber(doc.Pages[i], text, font, options.Position);
        }
        PdfOutput.SaveDocument(doc, outputPath);
    }

    /// <summary>
    /// Рисует текст так, как его увидит читатель — с учётом поворота страницы (/Rotate):
    /// считаем позицию на видимой странице и переводим в координаты неповёрнутой.
    /// </summary>
    private static void DrawNumber(PdfPage page, string text, XFont font, NumberPosition position)
    {
        var rotation = PdfOutput.NormalizeRotation(page.Rotate);
        var box = page.MediaBox;
        double w = box.Width, h = box.Height;
        var sideways = rotation is 90 or 270;
        double visW = sideways ? h : w, visH = sideways ? w : h;

        using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var size = g.MeasureString(text, font);

        // Центр надписи на видимой странице
        var vx = position switch
        {
            NumberPosition.BottomRight or NumberPosition.TopRight => visW - Margin - size.Width / 2,
            NumberPosition.BottomLeft => Margin + size.Width / 2,
            _ => visW / 2
        };
        var vy = position is NumberPosition.TopCenter or NumberPosition.TopRight
            ? Margin + size.Height / 2
            : visH - Margin - size.Height / 2;

        // Видимые координаты → координаты неповёрнутой страницы
        var (ux, uy) = rotation switch
        {
            90 => (vy, h - vx),
            180 => (w - vx, h - vy),
            270 => (w - vy, vx),
            _ => (vx, vy)
        };

        var state = g.Save();
        g.TranslateTransform(ux, uy);
        if (rotation != 0) g.RotateTransform(-rotation);
        g.DrawString(text, font, XBrushes.Black,
            new XRect(-size.Width / 2 - 2, -size.Height / 2, size.Width + 4, size.Height), XStringFormats.Center);
        g.Restore(state);
    }

    /// <summary>Шрифты Windows для рисования текста (настройка PDFsharp один раз на процесс).</summary>
    internal static void EnsureFonts()
    {
        lock (FontLock)
        {
            if (_fontsReady) return;
            try
            {
                if (GlobalFontSettings.FontResolver == null)
                    GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            }
            catch
            {
                // уже настроено где-то ещё — не мешаем
            }
            _fontsReady = true;
        }
    }
}
