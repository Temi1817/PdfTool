using System.Diagnostics;
using PdfTools.Core;

namespace PdfTool;

/// <summary>Главное окно. Только UI: вся работа с PDF — в PdfTools.Core.</summary>
internal sealed partial class MainForm : Form
{
    private const string PdfFilter = "PDF-файлы (*.pdf)|*.pdf|Все файлы (*.*)|*.*";

    private readonly string? _startupFile;
    private PdfDocumentModel? _model;
    private bool _busy;
    private bool _sourceOverwriteConfirmed;
    private bool _closeConfirmed;

    public MainForm(string? startupFile = null)
    {
        _startupFile = startupFile;
        BuildUi();
        WireEvents();
        UpdateUiState();
    }

    private void WireEvents()
    {
        _btnOpen.Click += async (_, _) => await OpenWithDialogAsync();
        _btnAppend.Click += async (_, _) => await AppendWithDialogAsync();
        _btnSave.ButtonClick += async (_, _) => await SaveAsync();
        _miSaveAs.Click += async (_, _) => await SaveAsAsync();
        _btnRotateLeft.Click += (_, _) => RotateSelected(-90);
        _btnRotateRight.Click += (_, _) => RotateSelected(90);
        _btnDelete.Click += (_, _) => DeleteSelected();
        _miSplitEach.Click += async (_, _) => await SplitEachPageAsync();
        _miExtract.Click += async (_, _) => await ExtractSelectedAsync();
        _miSplitRanges.Click += (_, _) => _txtRanges.Focus();
        _btnSplitRanges.Click += async (_, _) => await SplitByRangesAsync();
        _btnUndo.Click += (_, _) => Undo();
        _btnSelectAll.Click += (_, _) => { _view.SelectAllPages(); UpdateUiState(); };

        _txtRanges.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SplitByRangesAsync();
        };

        _view.SelectedIndexChanged += (_, _) => UpdateUiState();
        _view.MoveRequested += MovePages;
        _view.FilesDropped += async files => await HandleDroppedFilesAsync(files);
        _view.RenderProgress += (done, total) =>
            _lblRender.Text = done < total ? $"Миниатюры: {done} из {total}" : "";

        // Файлы можно бросать на любое место окна
        DragEnter += OnFileDragEnter;
        DragDrop += async (_, e) => await OnFileDropAsync(e);
        _emptyHint.DragEnter += OnFileDragEnter;
        _emptyHint.DragDrop += async (_, e) => await OnFileDropAsync(e);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!string.IsNullOrEmpty(_startupFile))
            await OpenFileAsync(_startupFile);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // В поле диапазонов стандартные Ctrl+A / Ctrl+Z / Delete работают с текстом
        var inText = ActiveControl is TextBoxBase;
        switch (keyData)
        {
            case Keys.Control | Keys.O: _btnOpen.PerformClick(); return true;
            case Keys.Control | Keys.S: if (_btnSave.Enabled) _btnSave.PerformButtonClick(); return true;
            case Keys.Control | Keys.Shift | Keys.S: if (_miSaveAs.Enabled) _miSaveAs.PerformClick(); return true;
            case Keys.Control | Keys.Z when !inText: if (_btnUndo.Enabled) Undo(); return true;
            case Keys.Control | Keys.A when !inText: if (_btnSelectAll.Enabled) _btnSelectAll.PerformClick(); return true;
            case Keys.Delete when !inText: if (_btnDelete.Enabled) DeleteSelected(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_closeConfirmed || _model?.IsModified != true) return;

        // Спрашиваем асинхронно (может понадобиться сохранение), затем закрываем повторно
        e.Cancel = true;
        if (_busy) return;
        if (await ConfirmDiscardChangesAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // ───────────── Открытие и добавление ─────────────

    private async Task OpenWithDialogAsync()
    {
        using var dlg = new OpenFileDialog { Filter = PdfFilter, Title = "Открыть PDF" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            await OpenFileAsync(dlg.FileName);
    }

    private async Task OpenFileAsync(string path)
    {
        if (!await ConfirmDiscardChangesAsync()) return;

        var model = await RunBusyAsync("Открытие файла…", () => PdfDocumentModel.Open(path));
        if (model == null) return;

        _model = model;
        _sourceOverwriteConfirmed = false;
        _view.SetDocument(model);
        UpdateUiState();
    }

    private async Task AppendWithDialogAsync()
    {
        if (_model == null) return;
        using var dlg = new OpenFileDialog { Filter = PdfFilter, Title = "Добавить PDF в конец", Multiselect = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            await AppendFilesAsync(dlg.FileNames);
    }

    private async Task AppendFilesAsync(IEnumerable<string> paths)
    {
        var model = _model;
        if (model == null) return;

        var firstNew = model.PageCount;
        foreach (var path in paths)
        {
            // Ошибка в одном файле не мешает добавить остальные
            await RunBusyAsync($"Добавление «{Path.GetFileName(path)}»…", () => PdfMerger.Append(model, path));
        }
        if (model.PageCount > firstNew)
            _view.RefreshPages(Enumerable.Range(firstNew, model.PageCount - firstNew));
        UpdateUiState();
    }

    private static void OnFileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async Task OnFileDropAsync(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            await HandleDroppedFilesAsync(files);
    }

    private async Task HandleDroppedFilesAsync(string[] files)
    {
        if (_busy || files.Length == 0) return;
        Activate();

        if (_model == null)
        {
            await OpenFileAsync(files[0]);
            if (_model != null && files.Length > 1)
                await AppendFilesAsync(files.Skip(1));
            return;
        }

        var answer = MessageBox.Show(this,
            "Добавить страницы в конец текущего документа?\n\n" +
            "Да — добавить в конец\nНет — открыть как новый документ",
            "PdfTool", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            await AppendFilesAsync(files);
        else if (answer == DialogResult.No)
            await OpenFileAsync(files[0]);
    }

    // ───────────── Правка страниц ─────────────

    private void RotateSelected(int degrees)
    {
        var selected = _view.SelectedPageIndices;
        if (_model == null || !HasSelection(selected)) return;
        if (RunSafe(() => PdfPageEditor.Rotate(_model, selected, degrees)))
            _view.RefreshPages(selected);
        UpdateUiState();
    }

    private void DeleteSelected()
    {
        var selected = _view.SelectedPageIndices;
        if (_model == null || !HasSelection(selected)) return;
        if (RunSafe(() => PdfPageEditor.Delete(_model, selected)))
        {
            // Выделяем страницу, которая встала на место первой удалённой
            var next = Math.Min(selected[0], _model.PageCount - 1);
            _view.RefreshPages(new[] { next });
            _lblRender.Text = $"Удалено страниц: {selected.Count}. Ctrl+Z — отменить";
        }
        UpdateUiState();
    }

    private void MovePages(IReadOnlyList<int> indices, int insertBefore)
    {
        if (_model == null || _busy) return;
        IReadOnlyList<int> newIndices = indices;
        if (RunSafe(() => newIndices = PdfPageEditor.Move(_model, indices, insertBefore)))
            _view.RefreshPages(newIndices);
        UpdateUiState();
    }

    private void Undo()
    {
        if (_model?.Undo() == true)
            _view.RefreshPages();
        UpdateUiState();
    }

    // ───────────── Сохранение ─────────────

    private async Task<bool> SaveAsync()
    {
        if (_model == null) return false;
        var path = _model.FilePath;

        if (_model.IsSourceFile(path) && !_sourceOverwriteConfirmed)
        {
            var answer = MessageBox.Show(this,
                $"Перезаписать исходный файл «{Path.GetFileName(path)}»?\n\n" +
                "Если хотите сохранить копию — нажмите «Нет» и выберите другое имя.",
                "Сохранение", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.No) return await SaveAsAsync();
            _sourceOverwriteConfirmed = true;
        }
        return await SaveToAsync(path);
    }

    private async Task<bool> SaveAsAsync()
    {
        if (_model == null) return false;
        using var dlg = new SaveFileDialog
        {
            Filter = PdfFilter,
            Title = "Сохранить как",
            InitialDirectory = Path.GetDirectoryName(_model.FilePath),
            FileName = _model.BaseName + "_изменён.pdf",
            OverwritePrompt = true // подтверждение перезаписи существующего файла
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        return await SaveToAsync(dlg.FileName);
    }

    private async Task<bool> SaveToAsync(string path)
    {
        var model = _model!;
        var ok = await RunBusyAsync("Сохранение…", () => { PdfPageEditor.Save(model, path); return true; });
        if (ok) _lblRender.Text = $"Сохранено: {Path.GetFileName(path)}";
        UpdateUiState();
        return ok;
    }

    /// <summary>true — можно продолжать (сохранили или пользователь отказался от изменений).</summary>
    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (_model?.IsModified != true) return true;
        var answer = MessageBox.Show(this,
            $"Сохранить изменения в «{Path.GetFileName(_model.FilePath)}»?",
            "PdfTool", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => await SaveAsync(),
            DialogResult.No => true,
            _ => false
        };
    }

    // ───────────── Деление ─────────────

    private async Task SplitByRangesAsync()
    {
        if (_model == null) return;
        if (!PageRangeParser.TryParse(_txtRanges.Text, _model.PageCount, out var ranges, out var error))
        {
            ShowWarning(error);
            _txtRanges.Focus();
            _txtRanges.SelectAll();
            return;
        }

        var model = _model;
        var dir = AskOutputFolder();
        if (dir == null) return;
        var planned = PdfSplitter.PlanRangeFiles(dir, model.BaseName, ranges);
        if (!ConfirmOverwrite(planned)) return;

        var files = await RunBusyAsync("Деление по диапазонам…",
            () => PdfSplitter.SplitByRanges(model, ranges, dir, model.BaseName));
        if (files != null) ReportCreated(files, dir);
    }

    private async Task SplitEachPageAsync()
    {
        if (_model == null) return;
        var model = _model;
        var dir = AskOutputFolder();
        if (dir == null) return;
        var planned = PdfSplitter.PlanEachPageFiles(dir, model.BaseName, model.PageCount);
        if (!ConfirmOverwrite(planned)) return;

        var files = await RunBusyAsync("Деление на отдельные страницы…",
            () => PdfSplitter.SplitEachPage(model, dir, model.BaseName));
        if (files != null) ReportCreated(files, dir);
    }

    private async Task ExtractSelectedAsync()
    {
        var selected = _view.SelectedPageIndices;
        if (_model == null || !HasSelection(selected)) return;
        var model = _model;

        using var dlg = new SaveFileDialog
        {
            Filter = PdfFilter,
            Title = "Сохранить выбранные страницы",
            InitialDirectory = Path.GetDirectoryName(model.FilePath),
            FileName = $"{model.BaseName}_выбранные.pdf",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var path = dlg.FileName;

        var ok = await RunBusyAsync("Сохранение выбранных страниц…",
            () => { PdfSplitter.ExtractPages(model, selected, path); return true; });
        if (ok) ReportCreated(new[] { path }, Path.GetDirectoryName(path)!);
    }

    private string? AskOutputFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Куда сохранить файлы",
            UseDescriptionForTitle = true,
            InitialDirectory = Path.GetDirectoryName(_model!.FilePath) ?? ""
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    private bool ConfirmOverwrite(IReadOnlyList<string> planned)
    {
        var existing = planned.Where(File.Exists).ToList();
        if (existing.Count == 0) return true;

        var list = string.Join("\n", existing.Take(10).Select(Path.GetFileName));
        if (existing.Count > 10) list += $"\n… и ещё {existing.Count - 10}";
        var sourceWarning = existing.Any(_model!.IsSourceFile) ? "\n\nСреди них есть ИСХОДНЫЙ файл!" : "";

        return MessageBox.Show(this,
            $"Эти файлы уже существуют и будут перезаписаны:\n\n{list}{sourceWarning}\n\nПродолжить?",
            "Перезапись файлов", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void ReportCreated(IReadOnlyList<string> files, string dir)
    {
        _lblRender.Text = $"Создано файлов: {files.Count}";
        var answer = MessageBox.Show(this,
            $"Готово. Создано файлов: {files.Count}\nПапка: {dir}\n\nОткрыть папку?",
            "PdfTool", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer != DialogResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{files[0]}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowWarning($"Не удалось открыть папку: {ex.Message}");
        }
    }

    // ───────────── Общее ─────────────

    /// <summary>
    /// Выполняет операцию в фоне, блокируя интерфейс. Ошибки показывает пользователю.
    /// Возвращает default(T), если операция не удалась.
    /// </summary>
    private async Task<T?> RunBusyAsync<T>(string status, Func<T> work)
    {
        if (_busy) return default;
        _busy = true;
        var oldStatus = _lblRender.Text;
        _lblRender.Text = status;
        UseWaitCursor = true;
        UpdateUiState();
        try
        {
            return await Task.Run(work);
        }
        catch (PdfToolException ex)
        {
            ShowError(ex.Message);
            return default;
        }
        catch (Exception ex)
        {
            ShowError($"Непредвиденная ошибка:\n{ex.Message}");
            return default;
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            if (_lblRender.Text == status) _lblRender.Text = oldStatus;
            UpdateUiState();
        }
    }

    /// <summary>Быстрая операция в UI-потоке с показом ошибки.</summary>
    private bool RunSafe(Action action)
    {
        if (_busy) return false;
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex is PdfToolException ? ex.Message : $"Непредвиденная ошибка:\n{ex.Message}");
            return false;
        }
    }

    private bool HasSelection(IReadOnlyList<int> selected)
    {
        if (selected.Count > 0) return true;
        ShowWarning("Сначала выберите страницы (клик, Ctrl+клик, Shift+клик или «Выделить всё»).");
        return false;
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "PdfTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void UpdateUiState()
    {
        var hasDoc = _model != null && !_busy;
        var hasSelection = hasDoc && _view.SelectedIndices.Count > 0;

        _btnOpen.Enabled = !_busy;
        _btnAppend.Enabled = hasDoc;
        _btnSave.Enabled = hasDoc;
        _miSaveAs.Enabled = hasDoc;
        _btnRotateLeft.Enabled = hasSelection;
        _btnRotateRight.Enabled = hasSelection;
        _btnDelete.Enabled = hasSelection;
        _btnSplit.Enabled = hasDoc;
        _miExtract.Enabled = hasSelection;
        _btnUndo.Enabled = hasDoc && _model!.CanUndo;
        _btnSelectAll.Enabled = hasDoc;
        _txtRanges.Enabled = hasDoc;
        _btnSplitRanges.Enabled = hasDoc;
        _view.Enabled = !_busy;
        _emptyHint.Visible = _model == null;

        if (_model == null)
        {
            Text = "PdfTool";
            _lblFile.Text = "Файл не открыт";
            _lblPages.Text = "";
            _lblSelected.Text = "";
            return;
        }

        var name = Path.GetFileName(_model.FilePath);
        Text = $"{name}{(_model.IsModified ? " *" : "")} — PdfTool";
        _lblFile.Text = _model.FilePath;
        _lblPages.Text = $"Страниц: {_model.PageCount}";
        _lblSelected.Text = $"Выбрано: {_view.SelectedIndices.Count}";
    }
}
