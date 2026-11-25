using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfTools.Core;

/// <summary>
/// Исходный PDF-файл, полностью загруженный в память.
/// Файл на диске после загрузки не держится открытым — его можно
/// перезаписать или открыть в другой программе.
/// </summary>
public sealed class PdfSource
{
    public int Id { get; }
    public string FilePath { get; }
    public int PageCount { get; }

    /// <summary>Содержимое файла. Используется и для сборки PDF, и для рендера миниатюр.</summary>
    internal byte[] Data { get; }

    private PdfSource(int id, string filePath, byte[] data, int pageCount)
    {
        Id = id;
        FilePath = filePath;
        Data = data;
        PageCount = pageCount;
    }

    internal static PdfSource Load(int id, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Не указан путь к файлу.");

        byte[] data;
        try
        {
            // FileShare.ReadWrite — чтобы читать файл, даже если он открыт в просмотрщике
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        catch (Exception ex)
        {
            throw PdfToolException.FromIo(ex, path, writing: false);
        }

        // Пробуем разобрать документ, чтобы сразу сообщить о битом файле или пароле
        using var doc = OpenForImport(data, path);
        if (doc.PageCount == 0)
            throw new PdfToolException(PdfErrorKind.Corrupted,
                $"В файле «{Path.GetFileName(path)}» нет ни одной страницы.");

        return new PdfSource(id, Path.GetFullPath(path), data, doc.PageCount);
    }

    /// <summary>Открывает копию документа для импорта страниц. Вызывающий обязан сделать Dispose.</summary>
    internal PdfDocument OpenForImport() => OpenForImport(Data, FilePath);

    private static PdfDocument OpenForImport(byte[] data, string path)
    {
        var name = Path.GetFileName(path);
        var passwordRequested = false;

        try
        {
            var stream = new MemoryStream(data, writable: false);
            var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import, args =>
            {
                // Пароль не поддерживаем — сразу прерываем открытие
                passwordRequested = true;
                args.Abort = true;
            });
            // При отмене ввода пароля PDFsharp возвращает null, а не исключение
            if (doc is null)
                throw new InvalidOperationException("Открытие прервано.");
            return doc;
        }
        catch (Exception ex) when (passwordRequested)
        {
            throw new PdfToolException(PdfErrorKind.PasswordProtected,
                $"Файл «{name}» защищён паролем.\nСнимите защиту в другой программе и откройте снова.", ex);
        }
        catch (Exception ex)
        {
            throw new PdfToolException(PdfErrorKind.Corrupted,
                $"Файл «{name}» повреждён или не является PDF.\n\nПодробности: {ex.Message}", ex);
        }
    }
}
