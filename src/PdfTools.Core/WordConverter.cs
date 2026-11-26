using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PdfTools.Core;

/// <summary>
/// Word → PDF через установленный Microsoft Word (COM, позднее связывание через dynamic,
/// без PIA). Если Word не установлен — IsWordInstalled() == false.
/// </summary>
public static class WordConverter
{
    /// <summary>Расширения файлов, которые открываются через Word.</summary>
    public static readonly IReadOnlyList<string> WordExtensions = new[] { ".docx", ".doc", ".docm", ".rtf", ".odt" };

    /// <summary>Это документ Word (по расширению)?</summary>
    public static bool IsWordFile(string path) =>
        WordExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private const int WdExportFormatPdf = 17;
    private const int WdDoNotSaveChanges = 0;
    private const int WdAlertsNone = 0;
    // Заведомо неверный пароль: для защищённого файла Word выдаст ошибку вместо скрытого окна ввода
    private const string NoPassword = "\u0001PdfTool-no-password";

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(5);

    public static bool IsWordInstalled()
    {
        try { return Type.GetTypeFromProgID("Word.Application") != null; }
        catch { return false; }
    }

    /// <summary>Документ Word (.docx, .doc, .rtf …) → PDF.</summary>
    public static void WordToPdf(string wordPath, string pdfPath)
    {
        if (!File.Exists(wordPath))
            throw new PdfToolException(PdfErrorKind.NotFound, $"Файл не найден:\n{wordPath}");

        var fullOut = Path.GetFullPath(pdfPath);
        var dir = Path.GetDirectoryName(fullOut)!;
        if (!Directory.Exists(dir))
            throw new PdfToolException(PdfErrorKind.NotFound, $"Папка для сохранения не найдена:\n{dir}");

        // Word пишет во временный файл рядом, затем заменяем целевой
        var tempOut = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp.pdf");
        try
        {
            RunWord(Path.GetFullPath(wordPath), doc => doc.ExportAsFixedFormat(tempOut, WdExportFormatPdf));
            if (!File.Exists(tempOut))
                throw new PdfToolException(PdfErrorKind.ConversionFailed, "Word не создал файл результата.");
            File.Move(tempOut, fullOut, overwrite: true);
        }
        catch (Exception ex)
        {
            throw PdfToolException.FromIo(ex, fullOut, writing: true);
        }
        finally
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { /* не критично */ }
        }
    }

    /// <summary>
    /// Запускает отдельный скрытый экземпляр Word в STA-потоке, открывает файл
    /// только для чтения и выполняет <paramref name="work"/>. Word всегда закрывается.
    /// </summary>
    private static void RunWord(string inputPath, Action<dynamic> work)
    {
        var wordType = Type.GetTypeFromProgID("Word.Application")
            ?? throw new PdfToolException(PdfErrorKind.WordUnavailable,
                "Microsoft Word не установлен — конвертация недоступна.");

        Exception? error = null;
        var ourWordPid = 0;

        var thread = new Thread(() =>
        {
            dynamic? word = null;
            dynamic? doc = null;
            try
            {
                var before = WordProcessIds();
                word = Activator.CreateInstance(wordType)!;
                ourWordPid = WordProcessIds().Except(before).FirstOrDefault();

                word.Visible = false;
                word.DisplayAlerts = WdAlertsNone;
                // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument,
                // PasswordTemplate, Revert, WritePasswordDocument, WritePasswordTemplate
                doc = word.Documents.Open(inputPath, false, true, false,
                    NoPassword, NoPassword, false, NoPassword, NoPassword);
                work(doc);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try { doc?.Close(WdDoNotSaveChanges); } catch { /* уже закрыт */ }
                // Закрываем Word, только если запустили его сами — чужие документы не трогаем
                if (ourWordPid != 0)
                {
                    try { word?.Quit(WdDoNotSaveChanges); } catch { /* Word сам завершится */ }
                }
                if (doc != null) Marshal.FinalReleaseComObject(doc);
                if (word != null) Marshal.FinalReleaseComObject(word);
                doc = null;
                word = null;
                // dynamic создаёт промежуточные COM-объекты (word.Documents и т.п.) —
                // пока они живы, Word не выгружается. Собираем их явно.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(ConversionTimeout))
        {
            KillWord(ourWordPid);
            throw new PdfToolException(PdfErrorKind.ConversionFailed,
                "Word не ответил за 5 минут — конвертация прервана.\n\n" +
                "Возможно, Word ждёт ответа на скрытый вопрос (например, «запустить в безопасном режиме?»). " +
                "Откройте Word вручную, ответьте на вопросы, закройте его и повторите.");
        }
        // Даём Word спокойно завершиться. Убивать его нельзя: после принудительного
        // завершения Word при следующем запуске спрашивает про безопасный режим и зависает.
        WaitForExit(ourWordPid);

        if (error != null)
            throw MapError(error, inputPath);
    }

    private static PdfToolException MapError(Exception ex, string inputPath)
    {
        if (ex is PdfToolException pte) return pte;
        var details = ex is COMException com ? com.Message.Trim() : ex.Message;
        return new PdfToolException(PdfErrorKind.ConversionFailed,
            $"Word не смог обработать файл «{Path.GetFileName(inputPath)}».\n" +
            "Возможно, файл повреждён, защищён паролем или занят.\n\n" +
            $"Подробности: {details}", ex);
    }

    private static HashSet<int> WordProcessIds()
    {
        var processes = Process.GetProcessesByName("WINWORD");
        var ids = processes.Select(p => p.Id).ToHashSet();
        foreach (var p in processes) p.Dispose();
        return ids;
    }

    private static void WaitForExit(int pid)
    {
        if (pid == 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(30_000);
        }
        catch { /* процесс уже завершился */ }
    }

    private static void KillWord(int pid)
    {
        if (pid == 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch { /* процесс уже завершился */ }
    }
}
