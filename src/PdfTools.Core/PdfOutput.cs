using PdfSharp.Pdf;

namespace PdfTools.Core;

/// <summary>
/// Сборка нового PDF из списка PageRef и безопасная запись на диск.
/// Общая для сохранения, деления и склейки.
/// </summary>
internal sealed class PdfOutput : IDisposable
{
    private readonly Func<int, PdfSource> _getSource;
    private readonly Dictionary<int, PdfDocument> _opened = new();

    /// <param name="getSource">Как найти исходник по Id (обычно model.GetSource).</param>
    public PdfOutput(Func<int, PdfSource> getSource)
    {
        _getSource = getSource;
    }

    /// <summary>Собирает страницы в новый PDF и записывает его в <paramref name="path"/>.</summary>
    public void Write(IReadOnlyList<PageRef> pages, string path)
    {
        using var output = Build(pages);
        WriteAtomically(path, stream => output.Save(stream, closeStream: false));
    }

    private PdfDocument Build(IReadOnlyList<PageRef> pages)
    {
        if (pages.Count == 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Нет страниц для сохранения.");

        var output = new PdfDocument();
        output.Info.Creator = "PdfTool";
        foreach (var pageRef in pages)
        {
            var source = GetOpened(pageRef.SourceId);
            var page = output.AddPage(source.Pages[pageRef.PageIndex]);
            if (pageRef.Rotation != 0)
                page.Rotate = NormalizeRotation(page.Rotate + pageRef.Rotation);
        }
        return output;
    }

    public void Dispose()
    {
        foreach (var doc in _opened.Values) doc.Dispose();
        _opened.Clear();
    }

    internal static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360;

    // Исходник открывается один раз на всю операцию (важно при делении на 100+ файлов)
    private PdfDocument GetOpened(int sourceId)
    {
        if (!_opened.TryGetValue(sourceId, out var doc))
        {
            doc = _getSource(sourceId).OpenForImport();
            _opened.Add(sourceId, doc);
        }
        return doc;
    }

    /// <summary>
    /// Пишет во временный файл рядом, потом заменяет целевой.
    /// Если что-то пошло не так — целевой файл остаётся нетронутым.
    /// </summary>
    internal static void WriteAtomically(string path, Action<Stream> write)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(fs);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            // Замена открытого в другой программе файла даёт «Access denied» — уточняем причину
            if (ex is UnauthorizedAccessException && IsLockedByOther(fullPath))
                throw new PdfToolException(PdfErrorKind.FileLocked,
                    $"Файл «{Path.GetFileName(fullPath)}» занят другой программой.\nЗакройте его там и повторите попытку.", ex);
            throw PdfToolException.FromIo(ex, fullPath, writing: true);
        }
    }

    private static bool IsLockedByOther(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* временный файл — не критично */ }
    }
}
