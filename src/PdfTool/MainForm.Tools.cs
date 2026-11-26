using PdfTools.Core;

namespace PdfTool;

/// <summary>Меню «Инструменты», просмотр страницы, последние файлы, «Очистить».</summary>
internal sealed partial class MainForm
{
    private readonly RecentFiles _recent = new();

    private void SetupTools()
    {
        _miCompress.Click += async (_, _) => await CompressAsync();
        _miNumbers.Click += async (_, _) => await AddPageNumbersAsync();
        _miToJpg.Click += async (_, _) => await ExportPagesAsync(PageImageFormat.Jpeg);
        _miToPng.Click += async (_, _) => await ExportPagesAsync(PageImageFormat.Png);
        _miCompress.ToolTipText = "Уменьшить размер файла: пережать крупные картинки и сканы";
        _miToJpg.ToolTipText = "Сохранить выбранные страницы картинками JPG (меньше размер)";
        _miToPng.ToolTipText = "Сохранить выбранные страницы картинками PNG (лучше качество)";

        _view.PageOpenRequested += OpenViewer;

        _btnOpen.DropDownItems.Add(new ToolStripMenuItem("Нет недавних файлов") { Enabled = false });
        _btnOpen.DropDownOpening += (_, _) => RebuildRecentMenu();
    }

    private void UpdateToolButtons()
    {
        var hasDoc = _model != null && !_busy;
        _btnTools.Enabled = !_busy;
        _miCompress.Enabled = hasDoc;
        _miNumbers.Enabled = hasDoc;
        _miToJpg.Enabled = hasDoc;
        _miToPng.Enabled = hasDoc;
    }

    /// <summary>Закрывает документ (файл на диске не трогается) и возвращает пустое окно.</summary>
    private async Task ClearDocumentAsync()
    {
        if (_model == null || !await ConfirmDiscardChangesAsync()) return;
        _model = null;
        _sourceOverwriteConfirmed = false;
        _txtRanges.Clear();
        _lblRender.Text = "";
        _view.SetDocument(null);
        UpdateUiState();
    }

    // ───────────── Просмотр ─────────────

    private void OpenViewer(int index)
    {
        if (_model == null || _busy || index < 0 || index >= _model.PageCount) return;
        using var viewer = new PageViewerForm(_model, index);
        viewer.ShowDialog(this);
    }

    // ───────────── Последние файлы ─────────────

    private void RebuildRecentMenu()
    {
        _btnOpen.DropDownItems.Clear();
        if (_recent.Items.Count == 0)
        {
            _btnOpen.DropDownItems.Add(new ToolStripMenuItem("Нет недавних файлов") { Enabled = false });
            return;
        }

        var n = 1;
        foreach (var path in _recent.Items)
        {
            var item = new ToolStripMenuItem($"&{n++ % 10}  {Path.GetFileName(path)}") { ToolTipText = path };
            item.Click += async (_, _) => await OpenRecentAsync(path);
            _btnOpen.DropDownItems.Add(item);
        }
        _btnOpen.DropDownItems.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem("Очистить список");
        clear.Click += (_, _) => _recent.Clear();
        _btnOpen.DropDownItems.Add(clear);
    }

    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowWarning($"Файл не найден — возможно, его переместили или удалили:\n{path}\n\nОн убран из списка.");
            _recent.Remove(path);
            return;
        }
        await OpenAnyFileAsync(path);
    }

    // ───────────── Сжатие ─────────────

    private async Task CompressAsync()
    {
        if (_model == null) return;
        var model = _model;
        var path = AskSavePath("Сохранить сжатый PDF", $"{model.BaseName}_сжатый.pdf");
        if (path == null) return;

        var result = await RunBusyAsync("Сжатие PDF…", () => PdfCompressor.Compress(model, path));
        if (result == null) return;

        if (!result.Saved)
        {
            MessageBox.Show(this,
                $"Уменьшить файл не получилось: он уже хорошо сжат ({FormatSize(result.OriginalBytes)}).\n" +
                "Сильнее всего сжимаются сканы и фотографии.\n\nНовый файл не создан.",
                "Сжатие PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var percent = 100 - result.CompressedBytes * 100 / Math.Max(1, result.OriginalBytes);
        _lblRender.Text = $"Сжато: {FormatSize(result.OriginalBytes)} → {FormatSize(result.CompressedBytes)}";
        AskOpenResult(path,
            $"Готово: {FormatSize(result.OriginalBytes)} → {FormatSize(result.CompressedBytes)} (меньше на {percent}%).\n" +
            $"Пережато картинок: {result.ImagesRecompressed}.", "Сжатие PDF");
    }

    // ───────────── Нумерация ─────────────

    private async Task AddPageNumbersAsync()
    {
        if (_model == null) return;
        var model = _model;

        PageNumberOptions options;
        using (var dlg = new PageNumberDialog(model.PageCount))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            options = dlg.Options;
        }

        var path = AskSavePath("Сохранить PDF с номерами страниц", $"{model.BaseName}_с_номерами.pdf");
        if (path == null) return;

        var ok = await RunBusyAsync("Нумерация страниц…",
            () => { PdfPageNumberer.AddNumbers(model, path, options); return true; });
        if (ok) AskOpenResult(path, $"Номера проставлены на {model.PageCount} стр.", "Нумерация страниц");
    }

    // ───────────── Страницы → картинки ─────────────

    private async Task ExportPagesAsync(PageImageFormat format)
    {
        if (_model == null) return;
        var model = _model;

        IReadOnlyList<int> pages = _view.SelectedPageIndices;
        if (pages.Count == 0)
        {
            var answer = MessageBox.Show(this,
                $"Страницы не выбраны. Сохранить все страницы ({model.PageCount} шт.) картинками?",
                "Страницы → картинки", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            pages = Enumerable.Range(0, model.PageCount).ToList();
        }

        var dir = AskOutputFolder();
        if (dir == null) return;
        var planned = PdfImageExporter.PlanFiles(model, pages, dir, model.BaseName, format);
        if (!ConfirmOverwrite(planned)) return;

        var files = await RunBusyAsync($"Сохранение картинок ({pages.Count} шт.)…",
            () => PdfImageExporter.Export(model, pages, dir, model.BaseName, format));
        if (files != null) ReportCreated(files, dir);
    }

    // ───────────── Общее ─────────────

    private string? AskSavePath(string title, string fileName)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = PdfFilter,
            Title = title,
            InitialDirectory = Path.GetDirectoryName(_model!.FilePath),
            FileName = fileName,
            OverwritePrompt = true
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    /// <summary>Сообщает о готовом файле и предлагает открыть его вместо текущего.</summary>
    private async void AskOpenResult(string path, string message, string caption)
    {
        var answer = MessageBox.Show(this, $"{message}\n\nФайл: {Path.GetFileName(path)}\n\nОткрыть его?",
            caption, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes) await OpenFileAsync(path);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} МБ",
        >= 1024 => $"{bytes / 1024.0:0} КБ",
        _ => $"{bytes} Б"
    };
}
