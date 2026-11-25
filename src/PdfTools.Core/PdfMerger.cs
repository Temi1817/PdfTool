namespace PdfTools.Core;

/// <summary>Склейка PDF-файлов.</summary>
public static class PdfMerger
{
    /// <summary>
    /// Добавляет все страницы файла в конец текущего документа (действие можно отменить).
    /// Возвращает количество добавленных страниц.
    /// </summary>
    public static int Append(PdfDocumentModel model, string path)
    {
        var source = model.AddSource(path);
        var result = new List<PageRef>(model.Pages);
        result.AddRange(Enumerable.Range(0, source.PageCount).Select(i => new PageRef(source.Id, i)));
        model.ReplacePages(result);
        return source.PageCount;
    }

    /// <summary>Склеивает несколько файлов в один, в указанном порядке, без открытия в редакторе.</summary>
    public static void MergeFiles(IEnumerable<string> inputPaths, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        var paths = inputPaths.ToList();
        if (paths.Count == 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Не выбрано ни одного файла для склейки.");

        var model = PdfDocumentModel.Open(paths[0]);
        foreach (var path in paths.Skip(1))
            Append(model, path);
        PdfPageEditor.Save(model, outputPath);
    }
}
