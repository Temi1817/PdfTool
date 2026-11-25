namespace PdfTools.Core;

/// <summary>
/// Деление документа на несколько PDF. Номера страниц — по текущему порядку
/// в редакторе (с учётом перестановок, поворотов и удалений).
/// Методы Plan* только вычисляют имена файлов — UI по ним проверяет,
/// не будет ли что-то перезаписано, и спрашивает пользователя.
/// </summary>
public static class PdfSplitter
{
    /// <summary>Имена файлов для деления по диапазонам: Имя_1-3.pdf, Имя_5.pdf …</summary>
    public static IReadOnlyList<string> PlanRangeFiles(string outputDir, string baseName,
        IReadOnlyList<PageRange> ranges) =>
        ranges.Select(r => Path.Combine(outputDir, $"{baseName}_{r}.pdf"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Имена файлов для деления «каждая страница отдельно»: Имя_1.pdf …</summary>
    public static IReadOnlyList<string> PlanEachPageFiles(string outputDir, string baseName, int pageCount) =>
        Enumerable.Range(1, pageCount)
            .Select(n => Path.Combine(outputDir, $"{baseName}_{n}.pdf"))
            .ToList();

    /// <summary>Отдельный файл на каждый диапазон. Возвращает пути созданных файлов.</summary>
    public static IReadOnlyList<string> SplitByRanges(PdfDocumentModel model, IReadOnlyList<PageRange> ranges,
        string outputDir, string baseName, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Не задано ни одного диапазона.");
        foreach (var r in ranges)
        {
            if (r.Start < 1 || r.End > model.PageCount || r.Start > r.End)
                throw new PdfToolException(PdfErrorKind.InvalidInput,
                    $"Диапазон {r} вне документа (страниц: {model.PageCount}).");
        }

        EnsureDirectory(outputDir);
        var created = new List<string>();
        using var output = new PdfOutput(model.GetSource);
        foreach (var range in ranges)
        {
            var path = Path.Combine(outputDir, $"{baseName}_{range}.pdf");
            var pages = model.Pages.Skip(range.Start - 1).Take(range.Count).ToList();
            output.Write(pages, path);
            if (!created.Contains(path, StringComparer.OrdinalIgnoreCase)) created.Add(path);
            progress?.Report(created.Count);
        }
        return created;
    }

    /// <summary>Каждая страница — в свой файл.</summary>
    public static IReadOnlyList<string> SplitEachPage(PdfDocumentModel model, string outputDir, string baseName,
        IProgress<int>? progress = null)
    {
        var ranges = Enumerable.Range(1, model.PageCount).Select(n => new PageRange(n, n)).ToList();
        return SplitByRanges(model, ranges, outputDir, baseName, progress);
    }

    /// <summary>Выбранные страницы (индексы с 0) — в один новый PDF, в текущем порядке.</summary>
    public static void ExtractPages(PdfDocumentModel model, IEnumerable<int> indices, string outputPath)
    {
        var pages = model.NormalizeIndices(indices).Select(i => model.Pages[i]).ToList();
        using var output = new PdfOutput(model.GetSource);
        output.Write(pages, outputPath);
    }

    private static void EnsureDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Не указана папка для сохранения.");
        if (!Directory.Exists(dir))
            throw new PdfToolException(PdfErrorKind.NotFound, $"Папка не найдена:\n{dir}");
    }
}
