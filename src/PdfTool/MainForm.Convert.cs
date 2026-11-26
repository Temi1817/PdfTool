using PdfTools.Core;

namespace PdfTool;

/// <summary>
/// Документы Word: превращаются в PDF через установленный Microsoft Word
/// и дальше открываются как обычные PDF (кнопкой, через «Открыть» или перетаскиванием).
/// </summary>
internal sealed partial class MainForm
{
    private const string WordPattern = "*.docx;*.doc;*.docm;*.rtf;*.odt";
    private const string WordFilter = $"Документы Word ({WordPattern})|{WordPattern}|Все файлы (*.*)|*.*";
    // Для «Открыть» и «Добавить PDF»: можно выбрать и PDF, и Word
    private const string OpenFilter =
        $"PDF и документы Word|*.pdf;{WordPattern}|PDF-файлы (*.pdf)|*.pdf|Документы Word|{WordPattern}|Все файлы (*.*)|*.*";

    private readonly bool _wordInstalled = WordConverter.IsWordInstalled();

    private void SetupConvertButtons()
    {
        _btnWordToPdf.Click += async (_, _) => await WordToPdfWithDialogAsync();
        _btnWordToPdf.ToolTipText = _wordInstalled
            ? "Превратить документ Word в PDF и открыть его.\nМожно просто перетащить файл Word в окно."
            : "Нужен установленный Microsoft Word";
    }

    private void UpdateConvertButtons() => _btnWordToPdf.Enabled = !_busy && _wordInstalled;

    private async Task WordToPdfWithDialogAsync()
    {
        using var dlg = new OpenFileDialog { Filter = WordFilter, Title = "Выберите документ Word" };
        if (dlg.ShowDialog(this) == DialogResult.OK) await OpenAnyFileAsync(dlg.FileName);
    }

    /// <summary>Открывает PDF, а документ Word сначала превращает в PDF.</summary>
    private async Task OpenAnyFileAsync(string path)
    {
        var pdfs = await PrepareFilesAsync(new[] { path });
        if (pdfs.Length > 0) await OpenFileAsync(pdfs[0]);
    }

    /// <summary>
    /// Документы Word превращает в PDF, остальные файлы возвращает как есть.
    /// Файлы, которые не удалось сконвертировать, пропускаются (ошибка уже показана).
    /// </summary>
    private async Task<string[]> PrepareFilesAsync(IEnumerable<string> files)
    {
        var result = new List<string>();
        foreach (var file in files)
        {
            if (!WordConverter.IsWordFile(file))
            {
                result.Add(file);
                continue;
            }
            var pdf = await ConvertWordToPdfAsync(file);
            if (pdf != null) result.Add(pdf);
        }
        return result.ToArray();
    }

    /// <summary>Конвертирует документ Word в PDF рядом с ним. Возвращает путь к PDF или null.</summary>
    private async Task<string?> ConvertWordToPdfAsync(string wordPath)
    {
        var name = Path.GetFileName(wordPath);
        if (!_wordInstalled)
        {
            ShowWarning($"«{name}» — документ Word.\nЧтобы превратить его в PDF, нужен установленный Microsoft Word.");
            return null;
        }

        var target = AskPdfPathFor(wordPath);
        if (target == null) return null;

        var ok = await RunBusyAsync($"«{name}» → PDF через Word…",
            () => { WordConverter.WordToPdf(wordPath, target); return true; });
        if (!ok) return null;
        _lblRender.Text = $"Создан PDF: {Path.GetFileName(target)}";
        return target;
    }

    /// <summary>
    /// Куда сохранить PDF: рядом с документом, с тем же именем.
    /// Если такой файл уже есть — спрашиваем: перезаписать или выбрать другое имя.
    /// </summary>
    private string? AskPdfPathFor(string wordPath)
    {
        var target = Path.ChangeExtension(Path.GetFullPath(wordPath), ".pdf");
        if (!File.Exists(target)) return target;

        var answer = MessageBox.Show(this,
            $"Рядом уже есть файл «{Path.GetFileName(target)}».\n\n" +
            "Да — перезаписать его\nНет — сохранить под другим именем",
            "Word → PDF", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes) return target;
        if (answer != DialogResult.No) return null;

        using var dlg = new SaveFileDialog
        {
            Filter = PdfFilter,
            Title = "Сохранить PDF",
            InitialDirectory = Path.GetDirectoryName(target),
            FileName = Path.GetFileNameWithoutExtension(target) + "_1.pdf",
            OverwritePrompt = true
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }
}
