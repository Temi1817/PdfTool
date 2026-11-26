using System.Runtime.InteropServices;
using PdfTools.Core;

namespace PdfTool;

/// <summary>
/// Файлы, которые не PDF: документы Word (через установленный Microsoft Word) и картинки
/// превращаются в PDF и дальше открываются как обычные PDF — кнопкой, через «Открыть» или перетаскиванием.
/// </summary>
internal sealed partial class MainForm
{
    private const string WordPattern = "*.docx;*.doc;*.docm;*.rtf;*.odt";
    private const string ImagePattern = "*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp";
    private const string WordFilter = $"Документы Word ({WordPattern})|{WordPattern}|Все файлы (*.*)|*.*";
    private const string ImageFilter = $"Картинки ({ImagePattern})|{ImagePattern}|Все файлы (*.*)|*.*";
    // Для «Открыть» и «Добавить»: PDF, Word и картинки
    private const string OpenFilter =
        $"PDF, Word и картинки|*.pdf;{WordPattern};{ImagePattern}|PDF-файлы (*.pdf)|*.pdf|" +
        $"Документы Word|{WordPattern}|Картинки|{ImagePattern}|Все файлы (*.*)|*.*";

    private readonly bool _wordInstalled = WordConverter.IsWordInstalled();

    private void SetupConvertButtons()
    {
        _btnWordToPdf.Click += async (_, _) => await WordToPdfWithDialogAsync();
        _btnWordToPdf.ToolTipText = _wordInstalled
            ? "Превратить документ Word в PDF и открыть его.\nМожно просто перетащить файл Word в окно."
            : "Нужен установленный Microsoft Word";
        _miImagesToPdf.Click += async (_, _) => await ImagesToPdfWithDialogAsync();
        _miImagesToPdf.ToolTipText = "Собрать фото или сканы в один PDF.\nМожно просто перетащить картинки в окно.";
        SetupTools();
    }

    private void UpdateConvertButtons()
    {
        _btnWordToPdf.Enabled = !_busy && _wordInstalled;
        _miImagesToPdf.Enabled = !_busy;
        UpdateToolButtons();
    }

    private async Task WordToPdfWithDialogAsync()
    {
        using var dlg = new OpenFileDialog { Filter = WordFilter, Title = "Выберите документ Word" };
        if (dlg.ShowDialog(this) == DialogResult.OK) await OpenAnyFileAsync(dlg.FileName);
    }

    private async Task ImagesToPdfWithDialogAsync()
    {
        using var dlg = new OpenFileDialog { Filter = ImageFilter, Title = "Выберите картинки", Multiselect = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) await HandleDroppedFilesAsync(dlg.FileNames);
    }

    /// <summary>Открывает PDF; документ Word или картинку сначала превращает в PDF.</summary>
    private async Task OpenAnyFileAsync(string path)
    {
        var pdfs = await PrepareFilesAsync(new[] { path });
        if (pdfs.Length > 0) await OpenFileAsync(pdfs[0]);
    }

    /// <summary>
    /// Документы Word и картинки превращает в PDF, остальные файлы возвращает как есть.
    /// Все картинки собираются в один PDF (по имени, как в Проводнике) на месте первой из них.
    /// Файлы, которые не удалось превратить, пропускаются (ошибка уже показана).
    /// </summary>
    private async Task<string[]> PrepareFilesAsync(IEnumerable<string> files)
    {
        var list = files.ToList();
        var images = list.Where(ImagePdfBuilder.IsImageFile).ToList();
        images.Sort(StrCmpLogicalW);

        var result = new List<string>();
        var imagesDone = false;
        foreach (var file in list)
        {
            if (ImagePdfBuilder.IsImageFile(file))
            {
                if (imagesDone) continue;
                imagesDone = true;
                var pdf = await ConvertImagesToPdfAsync(images);
                if (pdf != null) result.Add(pdf);
            }
            else if (WordConverter.IsWordFile(file))
            {
                var pdf = await ConvertWordToPdfAsync(file);
                if (pdf != null) result.Add(pdf);
            }
            else
            {
                result.Add(file);
            }
        }
        return result.ToArray();
    }

    private async Task<string?> ConvertImagesToPdfAsync(IReadOnlyList<string> images)
    {
        var target = AskPdfPathFor(images[0]);
        if (target == null) return null;

        var ok = await RunBusyAsync($"Картинки → PDF ({images.Count} шт.)…",
            () => { ImagePdfBuilder.Create(images, target); return true; });
        if (!ok) return null;
        _lblRender.Text = $"Создан PDF: {Path.GetFileName(target)}";
        return target;
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
    /// Куда сохранить PDF: рядом с исходным файлом, с тем же именем.
    /// Если такой файл уже есть — спрашиваем: перезаписать или выбрать другое имя.
    /// </summary>
    private string? AskPdfPathFor(string sourcePath)
    {
        var target = Path.ChangeExtension(Path.GetFullPath(sourcePath), ".pdf");
        if (!File.Exists(target)) return target;

        var answer = MessageBox.Show(this,
            $"Рядом уже есть файл «{Path.GetFileName(target)}».\n\n" +
            "Да — перезаписать его\nНет — сохранить под другим именем",
            "Создание PDF", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
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

    // Сортировка имён как в Проводнике: «фото2» раньше «фото10»
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);
}
