using PdfTools.Core;

namespace PdfTool;

/// <summary>
/// Просмотр страницы крупно. ← → / PageUp PageDown — листать, + − и Ctrl+колёсико — масштаб,
/// 0 — по размеру окна, Esc — закрыть.
/// </summary>
internal sealed class PageViewerForm : Form
{
    private const double MinZoom = 0.25, MaxZoom = 5;
    private const int MaxRenderSide = 6000;

    private readonly PdfDocumentModel _model;
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(70, 70, 70) };
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.AutoSize };
    private readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
    private readonly ToolStripButton _btnPrev = new("◀ Назад") { ToolTipText = "Предыдущая страница (←)" };
    private readonly ToolStripButton _btnNext = new("Вперёд ▶") { ToolTipText = "Следующая страница (→)" };
    private readonly ToolStripLabel _lblPage = new();
    private readonly ToolStripButton _btnZoomOut = new("−") { ToolTipText = "Уменьшить (−, Ctrl+колёсико)" };
    private readonly ToolStripLabel _lblZoom = new();
    private readonly ToolStripButton _btnZoomIn = new("+") { ToolTipText = "Увеличить (+, Ctrl+колёсико)" };
    private readonly ToolStripButton _btnFit = new("По размеру окна") { ToolTipText = "Вписать страницу в окно (0)" };
    private readonly ToolStripLabel _lblStatus = new() { ForeColor = Color.DarkRed };
    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 150 };

    private int _index;
    private double _zoom = 1.0;
    private int _renderVersion;

    public PageViewerForm(PdfDocumentModel model, int startIndex)
    {
        _model = model;
        _index = Math.Clamp(startIndex, 0, model.PageCount - 1);

        Text = $"{Path.GetFileName(model.FilePath)} — просмотр";
        Font = SystemFonts.MessageBoxFont ?? Font;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 800);
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        ShowInTaskbar = false;

        _bar.Items.AddRange(new ToolStripItem[]
        {
            _btnPrev, _lblPage, _btnNext, new ToolStripSeparator(),
            _btnZoomOut, _lblZoom, _btnZoomIn, _btnFit, new ToolStripSeparator(), _lblStatus
        });
        _scroll.Controls.Add(_picture);
        Controls.Add(_scroll);
        Controls.Add(_bar);

        _btnPrev.Click += (_, _) => Go(_index - 1);
        _btnNext.Click += (_, _) => Go(_index + 1);
        _btnZoomIn.Click += (_, _) => Zoom(_zoom * 1.25);
        _btnZoomOut.Click += (_, _) => Zoom(_zoom / 1.25);
        _btnFit.Click += (_, _) => Zoom(1.0);
        _scroll.MouseWheel += OnWheel;
        _scroll.Resize += (_, _) => { CenterPicture(); _resizeTimer.Stop(); _resizeTimer.Start(); };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); Render(); };
        Shown += (_, _) => Render();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Left or Keys.PageUp: Go(_index - 1); return true;
            case Keys.Right or Keys.PageDown or Keys.Space: Go(_index + 1); return true;
            case Keys.Home: Go(0); return true;
            case Keys.End: Go(_model.PageCount - 1); return true;
            case Keys.Add or Keys.Oemplus: Zoom(_zoom * 1.25); return true;
            case Keys.Subtract or Keys.OemMinus: Zoom(_zoom / 1.25); return true;
            case Keys.D0 or Keys.NumPad0: Zoom(1.0); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderVersion++; // отменяем запоздавший рендер
            _resizeTimer.Dispose();
            _picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0) return;
        ((HandledMouseEventArgs)e).Handled = true; // не прокручиваем, а масштабируем
        Zoom(e.Delta > 0 ? _zoom * 1.25 : _zoom / 1.25);
    }

    private void Go(int index)
    {
        index = Math.Clamp(index, 0, _model.PageCount - 1);
        if (index == _index) return;
        _index = index;
        _scroll.AutoScrollPosition = Point.Empty;
        Render();
    }

    private void Zoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.001 && _picture.Image != null) return;
        _zoom = zoom;
        Render();
    }

    /// <summary>Рендерит текущую страницу в фоне; устаревшие результаты отбрасываются.</summary>
    private async void Render()
    {
        var version = ++_renderVersion;
        var page = _model.Pages[_index];
        UpdateLabels();

        var area = _scroll.ClientSize;
        var w = Math.Clamp((int)((area.Width - 24) * _zoom), 50, MaxRenderSide);
        var h = Math.Clamp((int)((area.Height - 24) * _zoom), 50, MaxRenderSide);

        try
        {
            var png = await Task.Run(() => PdfRenderer.RenderThumbnailPng(_model, page, w, h));
            if (version != _renderVersion || IsDisposed) return;

            using var ms = new MemoryStream(png);
            using var decoded = new Bitmap(ms);
            var old = _picture.Image;
            _picture.Image = new Bitmap(decoded);
            old?.Dispose();
            _lblStatus.Text = "";
            CenterPicture();
        }
        catch (Exception ex)
        {
            if (version == _renderVersion && !IsDisposed)
                _lblStatus.Text = ex is PdfToolException ? ex.Message : "Не удалось отрисовать страницу";
        }
    }

    private void CenterPicture()
    {
        var area = _scroll.ClientSize;
        var x = Math.Max(0, (area.Width - _picture.Width) / 2);
        var y = Math.Max(0, (area.Height - _picture.Height) / 2);
        _picture.Location = new Point(x + _scroll.AutoScrollPosition.X, y + _scroll.AutoScrollPosition.Y);
    }

    private void UpdateLabels()
    {
        _lblPage.Text = $"Стр. {_index + 1} из {_model.PageCount}";
        _lblZoom.Text = $"{_zoom * 100:0}%";
        _btnPrev.Enabled = _index > 0;
        _btnNext.Enabled = _index < _model.PageCount - 1;
    }
}
