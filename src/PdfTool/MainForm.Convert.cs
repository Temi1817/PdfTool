using System.Diagnostics;
using PdfTools.Core;

namespace PdfTool;

/// <summary>Конвертация Word ↔ PDF через установленный Microsoft Word.</summary>
internal sealed partial class MainForm
{
    private const string WordFilter =
        "Документы Word (*.docx;*.doc;*.rtf)|*.docx;*.doc;*.docm;*.rtf;*.odt|Все файлы (*.*)|*.*";

    private readonly bool _wordInstalled = WordConverter.IsWordInstalled();

    private void SetupConvertButtons()
    {
        _btnPdfToWord.Click += async (_, _) => await PdfToWordAsync();
        _btnWordToPdf.Click += async (_, _) => await WordToPdfAsync();

        _btnPdfToWord.ToolTipText = "Сохранить текущий документ как файл Word (.docx)";
        _btnWordToPdf.ToolTipText = "Превратить документ Word в PDF и открыть его" +
                                    (_wordInstalled ? "" : "\nНужен установленный Microsoft Word");
    }

    private void UpdateConvertButtons(bool hasDoc)
    {
        _btnPdfToWord.Enabled = hasDoc; // своя конвертация, Word не нужен
        _btnWordToPdf.Enabled = !_busy && _wordInstalled;
    }

    private async Task PdfToWordAsync()
    {
        if (_model == null) return;
        var model = _model;

        using var dlg = new SaveFileDialog
        {
            Filter = "Документ Word (*.docx)|*.docx",
            Title = "Сохранить как документ Word",
            InitialDirectory = Path.GetDirectoryName(model.FilePath),
            FileName = model.BaseName + ".docx",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var path = dlg.FileName;

        var result = await RunBusyAsync("Конвертация в Word…",
            () => PdfToDocxConverter.Convert(model, path));
        if (result == null) return;

        _lblRender.Text = $"Создан: {Path.GetFileName(path)}";
        var scans = result.PagesAsImages == 0
            ? ""
            : $"Страниц без текста (сканы): {result.PagesAsImages} — вставлены картинками, их текст править нельзя.\n\n";
        var answer = MessageBox.Show(this,
            $"Готово: {Path.GetFileName(path)}\n\n{scans}" +
            "Текст можно редактировать. Сложная вёрстка (колонки, текст поверх картинок) упрощается.\n\n" +
            "Открыть документ?",
            "PDF → Word", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes) OpenWithShell(path);
    }

    private async Task WordToPdfAsync()
    {
        using var open = new OpenFileDialog { Filter = WordFilter, Title = "Выберите документ Word" };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        var source = open.FileName;

        using var save = new SaveFileDialog
        {
            Filter = PdfFilter,
            Title = "Сохранить PDF",
            InitialDirectory = Path.GetDirectoryName(source),
            FileName = Path.GetFileNameWithoutExtension(source) + ".pdf",
            OverwritePrompt = true
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        var target = save.FileName;

        var ok = await RunBusyAsync("Конвертация в PDF…",
            () => { WordConverter.WordToPdf(source, target); return true; });
        if (ok) await OpenFileAsync(target); // сразу открываем результат
    }

    private void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowWarning($"Не удалось открыть файл: {ex.Message}");
        }
    }
}
