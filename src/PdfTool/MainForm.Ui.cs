namespace PdfTool;

/// <summary>Создание и раскладка элементов окна (без дизайнера — всё в коде).</summary>
internal sealed partial class MainForm
{
    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _btnOpen = new("Открыть");
    private readonly ToolStripButton _btnAppend = new("Добавить PDF");
    private readonly ToolStripSplitButton _btnSave = new("Сохранить");
    private readonly ToolStripMenuItem _miSaveAs = new("Сохранить как…");
    private readonly ToolStripButton _btnRotateLeft = new("⟲ Влево");
    private readonly ToolStripButton _btnRotateRight = new("⟳ Вправо");
    private readonly ToolStripButton _btnDelete = new("Удалить");
    private readonly ToolStripDropDownButton _btnSplit = new("Разделить");
    private readonly ToolStripMenuItem _miSplitEach = new("Каждая страница — отдельный файл…");
    private readonly ToolStripMenuItem _miExtract = new("Выбранные страницы — в новый PDF…");
    private readonly ToolStripMenuItem _miSplitRanges = new("По диапазонам (поле внизу)…");
    private readonly ToolStripButton _btnUndo = new("Отменить");
    private readonly ToolStripButton _btnSelectAll = new("Выделить всё");

    private readonly ThumbnailView _view = new();
    private readonly Label _emptyHint = new();

    private readonly TableLayoutPanel _bottomPanel = new();
    private readonly TextBox _txtRanges = new();
    private readonly Button _btnSplitRanges = new();

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _lblFile = new();
    private readonly ToolStripStatusLabel _lblPages = new();
    private readonly ToolStripStatusLabel _lblSelected = new();
    private readonly ToolStripStatusLabel _lblRender = new();

    private void BuildUi()
    {
        SuspendLayout();

        Text = "PdfTool";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(640, 420);
        Size = new Size(1100, 760);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        KeyPreview = true;

        // ── Панель инструментов ──
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.Padding = new Padding(4, 2, 4, 2);
        _btnOpen.ToolTipText = "Открыть PDF (Ctrl+O)";
        _btnAppend.ToolTipText = "Добавить страницы другого PDF в конец документа";
        _btnSave.ToolTipText = "Сохранить (Ctrl+S)";
        _miSaveAs.ShortcutKeyDisplayString = "Ctrl+Shift+S";
        _btnSave.DropDownItems.Add(_miSaveAs);
        _btnRotateLeft.ToolTipText = "Повернуть выбранные страницы на 90° против часовой";
        _btnRotateRight.ToolTipText = "Повернуть выбранные страницы на 90° по часовой";
        _btnDelete.ToolTipText = "Удалить выбранные страницы (Delete)";
        _btnSplit.DropDownItems.AddRange(new ToolStripItem[] { _miSplitEach, _miExtract, _miSplitRanges });
        _btnUndo.ToolTipText = "Отменить последнее действие (Ctrl+Z)";
        _btnSelectAll.ToolTipText = "Выделить все страницы (Ctrl+A)";

        _toolStrip.Items.AddRange(new ToolStripItem[]
        {
            _btnOpen, _btnAppend, _btnSave,
            new ToolStripSeparator(),
            _btnRotateLeft, _btnRotateRight, _btnDelete,
            new ToolStripSeparator(),
            _btnSplit,
            new ToolStripSeparator(),
            _btnUndo, _btnSelectAll
            // Версия 2: здесь появится группа «Конвертация» (Word → PDF, PDF → Word)
        });

        // ── Миниатюры ──
        _view.Dock = DockStyle.Fill;
        _emptyHint.Dock = DockStyle.Fill;
        _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyHint.ForeColor = SystemColors.GrayText;
        _emptyHint.Text = "Перетащите PDF-файл сюда\nили нажмите «Открыть»";
        _emptyHint.Font = new Font(Font.FontFamily, Font.Size * 1.4f);
        _emptyHint.AllowDrop = true;
        _view.Controls.Add(_emptyHint);

        // ── Нижняя панель: диапазоны ──
        _bottomPanel.Dock = DockStyle.Bottom;
        _bottomPanel.AutoSize = true;
        _bottomPanel.Padding = new Padding(8, 6, 8, 6);
        _bottomPanel.ColumnCount = 3;
        _bottomPanel.RowCount = 1;
        _bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lblRanges = new Label
        {
            Text = "Диапазоны:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 6, 0)
        };
        _txtRanges.Dock = DockStyle.Fill;
        _txtRanges.PlaceholderText = "например: 1-3, 4-7, 8-10";
        _btnSplitRanges.Text = "Разделить по диапазонам";
        _btnSplitRanges.AutoSize = true;
        _btnSplitRanges.Margin = new Padding(6, 0, 0, 0);

        _bottomPanel.Controls.Add(lblRanges, 0, 0);
        _bottomPanel.Controls.Add(_txtRanges, 1, 0);
        _bottomPanel.Controls.Add(_btnSplitRanges, 2, 0);

        // ── Строка состояния ──
        _lblFile.Spring = true;
        _lblFile.TextAlign = ContentAlignment.MiddleLeft;
        _lblPages.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblSelected.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblRender.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _statusStrip.Items.AddRange(new ToolStripItem[] { _lblFile, _lblPages, _lblSelected, _lblRender });

        // Порядок важен для Dock: сначала Fill, потом края
        Controls.Add(_view);
        Controls.Add(_bottomPanel);
        Controls.Add(_toolStrip);
        Controls.Add(_statusStrip);

        ResumeLayout(performLayout: true);
    }
}
