namespace PdfTools.Core;

/// <summary>Тип ошибки — чтобы UI мог при желании по-разному реагировать.</summary>
public enum PdfErrorKind
{
    InvalidInput,
    NotFound,
    AccessDenied,
    FileLocked,
    Corrupted,
    PasswordProtected,
    IoError,
    WordUnavailable,
    ConversionFailed
}

/// <summary>
/// Единственное исключение, которое Core выбрасывает наружу.
/// Message всегда на русском и готово к показу пользователю.
/// </summary>
public sealed class PdfToolException : Exception
{
    public PdfErrorKind Kind { get; }

    public PdfToolException(PdfErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>Превращает системное исключение работы с файлом в понятное сообщение.</summary>
    internal static PdfToolException FromIo(Exception ex, string path, bool writing)
    {
        var name = Path.GetFileName(path);
        return ex switch
        {
            PdfToolException pte => pte,
            FileNotFoundException or DirectoryNotFoundException => new PdfToolException(
                PdfErrorKind.NotFound,
                writing ? $"Папка для сохранения не найдена:\n{path}" : $"Файл не найден:\n{path}", ex),
            UnauthorizedAccessException => new PdfToolException(
                PdfErrorKind.AccessDenied,
                $"Нет доступа к файлу «{name}».\nВозможно, файл только для чтения или не хватает прав.", ex),
            IOException io when IsLocked(io) => new PdfToolException(
                PdfErrorKind.FileLocked,
                $"Файл «{name}» занят другой программой.\nЗакройте его там и повторите попытку.", ex),
            IOException => new PdfToolException(
                PdfErrorKind.IoError,
                $"Ошибка при {(writing ? "записи" : "чтении")} файла «{name}»:\n{ex.Message}", ex),
            _ => new PdfToolException(
                PdfErrorKind.IoError,
                $"Не удалось {(writing ? "сохранить" : "прочитать")} файл «{name}»:\n{ex.Message}", ex)
        };
    }

    // ERROR_SHARING_VIOLATION (32) и ERROR_LOCK_VIOLATION (33)
    private static bool IsLocked(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}
