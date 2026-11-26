using PdfTools.Core;

namespace PdfTool;

/// <summary>
/// Установка подписи/печати: страница крупно, подпись перетаскивается мышью и меняет размер за уголок.
/// Результат — готовая картинка (PNG), место на видимой странице и список страниц.
/// </summary>
internal sealed class StampPlacementForm : Form
{
    private readonly PdfDocumentModel _model;
    private readonly int _pageIndex;
    private readonly IReadOnlyList<int> _selected;
    private readonly byte[] _fileBytes;

    private readonly StampCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _removeBackground = new() { Text = "Убрать белый фон", Checked = true, AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _ok = new() { Text = "Готово", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false };
    private readonly List<IReadOnlyList<int>> _targetPages = new();
    private int _prepareVersion;

    public StampPlacementForm(PdfDocumentModel model, int pageIndex, IReadOnlyList<int> selected, byte[] fileBytes, string fileName)
    {
        _model = model;
        _pageIndex = pageIndex;
        _selected = selected;
        _fileBytes = fileBytes;

        Text = $"Подпись / печать — {fileName}";
        Font = SystemFonts.MessageBoxFont ?? Font;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 860);
        MinimumSize = new Size(600, 500);
        ShowInTaskbar = false;
        KeyPreview = true;

        AddTarget($"Только на эту страницу (стр. {pageIndex + 1})", new[] { pageIndex });
        if (selected.Count > 1) AddTarget($"На выбранные страницы ({selected.Count} шт.)", selected);
        if (model.PageCount > 1) AddTarget($"На все страницы ({model.PageCount} шт.)", Enumerable.Range(0, model.PageCount).ToList());
        _target.SelectedIndex = 0;

        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = _ok;
        CancelButton = cancel;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 2), WrapContents = true };
        top.Controls.Add(new Label { Text = "Куда:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        top.Controls.Add(_target);
        _removeBackground.Margin = new Padding(16, 5, 0, 0);
        top.Controls.Add(_removeBackground);

        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 24, Padding = new Padding(10, 4, 8, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Перетащите подпись мышкой. Чтобы изменить размер — потяните за синий уголок."
        };

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { cancel, _ok, _status });
        _status.Margin = new Padding(0, 7, 16, 0);

        Controls.Add(_canvas);
        Controls.Add(hint);
        Controls.Add(top);
        Controls.Add(bottom);

        _removeBackground.CheckedChanged += (_, _) => PrepareStamp();
        Shown += (_, _) => { RenderPage(); PrepareStamp(); };
    }

    /// <summary>Готовая картинка подписи (PNG) — после «Готово».</summary>
    public byte[]? ProcessedPng { get; private set; }

    /// <summary>Место подписи на видимой странице (доли 0..1).</summary>
    public PageRect VisibleRect => _canvas.StampRect;

    public IReadOnlyList<int> TargetPages => _targetPages[_target.SelectedIndex];

    private void AddTarget(string title, IReadOnlyList<int> pages)
    {
        _target.Items.Add(title);
        _targetPages.Add(pages);
    }

    private async void RenderPage()
    {
        try
        {
            var page = _model.Pages[_pageIndex];
            var png = await Task.Run(() => PdfRenderer.RenderPageImage(_model, page, 110, jpeg: false));
            if (IsDisposed) return;
            _canvas.SetPage(ToBitmap(png));
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = $"Не удалось показать страницу: {ex.Message}";
        }
    }

    /// <summary>Готовит картинку подписи в фоне (убрать фон, обрезать поля).</summary>
    private async void PrepareStamp()
    {
        var version = ++_prepareVersion;
        var removeBackground = _removeBackground.Checked;
        _ok.Enabled = false;
        _status.ForeColor = SystemColors.GrayText;
        _status.Text = "Обработка картинки…";
        try
        {
            var png = await Task.Run(() => StampImage.Prepare(_fileBytes, removeBackground));
            if (version != _prepareVersion || IsDisposed) return;
            ProcessedPng = png;
            _canvas.SetStamp(ToBitmap(png));
            _status.Text = "";
            _ok.Enabled = true;
        }
        catch (Exception ex)
        {
            if (version != _prepareVersion || IsDisposed) return;
            ProcessedPng = null;
            _canvas.SetStamp(null);
            _status.ForeColor = Color.DarkRed;
            _status.Text = ex is PdfToolException ? ex.Message.Replace('\n', ' ') : $"Ошибка: {ex.Message}";
        }
    }

    private static Bitmap ToBitmap(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var decoded = new Bitmap(ms);
        return new Bitmap(decoded);
    }
}
