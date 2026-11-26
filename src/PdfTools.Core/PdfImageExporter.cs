namespace PdfTools.Core;

public enum PageImageFormat
{
    Jpeg,
    Png
}

/// <summary>Сохранение страниц картинками: Имя_стр1.jpg, Имя_стр2.jpg …</summary>
public static class PdfImageExporter
{
    /// <summary>150 DPI — читаемо на телефоне и не слишком тяжело для мессенджеров.</summary>
    public const int DefaultDpi = 150;

    public static string Extension(PageImageFormat format) => format == PageImageFormat.Jpeg ? ".jpg" : ".png";

    /// <summary>Имена файлов для страниц (индексы с 0, номер в имени — с 1, по текущему порядку).</summary>
    public static IReadOnlyList<string> PlanFiles(PdfDocumentModel model, IEnumerable<int> indices,
        string outputDir, string baseName, PageImageFormat format) =>
        model.NormalizeIndices(indices)
            .Select(i => Path.Combine(outputDir, $"{baseName}_стр{i + 1}{Extension(format)}"))
            .ToList();

    public static IReadOnlyList<string> Export(PdfDocumentModel model, IEnumerable<int> indices,
        string outputDir, string baseName, PageImageFormat format, int dpi = DefaultDpi)
    {
        if (!Directory.Exists(outputDir))
            throw new PdfToolException(PdfErrorKind.NotFound, $"Папка не найдена:\n{outputDir}");
        if (dpi is < 36 or > 600)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Разрешение должно быть от 36 до 600 DPI.");

        var selected = model.NormalizeIndices(indices);
        var files = PlanFiles(model, selected, outputDir, baseName, format);
        for (var k = 0; k < selected.Count; k++)
        {
            byte[] data;
            try
            {
                data = PdfRenderer.RenderPageImage(model, model.Pages[selected[k]], dpi, format == PageImageFormat.Jpeg);
            }
            catch (Exception ex) when (ex is not PdfToolException)
            {
                throw new PdfToolException(PdfErrorKind.Corrupted,
                    $"Не удалось отрисовать страницу {selected[k] + 1}: {ex.Message}", ex);
            }
            PdfOutput.WriteAtomically(files[k], s => s.Write(data));
        }
        return files;
    }
}
