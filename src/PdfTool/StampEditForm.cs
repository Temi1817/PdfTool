using System.Drawing.Drawing2D;
using PdfTools.Core;

namespace PdfTool;

/// <summary>
/// Ручная правка картинки подписи/печати: оставить часть рамкой или кругом, стереть лишнее ластиком.
/// Прозрачные места показаны «шахматкой».
/// </summary>
internal sealed class StampEditForm : Form
{
    private enum Tool { Rectangle, Ellipse, Eraser }

    private readonly Stack<byte[]> _history = new();
    private readonly byte[] _original;
    private byte[] _current;

    private readonly EditCanvas _canvas;
    private readonly ToolStripButton _btnRect = new("▭ Рамка") { CheckOnClick = true, ToolTipText = "Обведите нужную часть — остальное удалится" };
    private readonly ToolStripButton _btnEllipse = new("◯ Круг") { CheckOnClick = true, ToolTipText = "Обведите круглую печать — всё вокруг удалится" };
    private readonly ToolStripButton _btnEraser = new("Ластик") { CheckOnClick = true, ToolTipText = "Сотрите лишнее, водя мышкой" };
    private readonly ToolStripComboBox _eraserSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 110 };
    private readonly ToolStripButton _btnUndo = new("↶ Отменить шаг") { ToolTipText = "Ctrl+Z" };
    private readonly ToolStripButton _btnReset = new("Сначала");
    private readonly Label _hint = new() { Dock = DockStyle.Top, Height = 24, Padding = new Padding(10, 4, 8, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DarkRed, Margin = new Padding(0, 7, 16, 0) };

    private static readonly (string Title, int Radius)[] EraserSizes =
        { ("Маленький", 5), ("Средний", 12), ("Большой", 25), ("Очень большой", 45) };

    public StampEditForm(byte[] png)
    {
        _original = png;
        _current = png;
        _canvas = new EditCanvas(this) { Dock = DockStyle.Fill };

        Text = "Обрезать / стереть";
        Font = SystemFonts.MessageBoxFont ?? Font;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 760);
        MinimumSize = new Size(500, 400);
        ShowInTaskbar = false;
        KeyPreview = true;

        foreach (var (title, _) in EraserSizes) _eraserSize.Items.Add(title);
        _eraserSize.SelectedIndex = 1;

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
        bar.Items.AddRange(new ToolStripItem[]
        {
            _btnRect, _btnEllipse, _btnEraser, new ToolStripLabel("Размер ластика:"), _eraserSize,
            new ToolStripSeparator(), _btnUndo, _btnReset
        });

        var ok = new Button { Text = "Применить", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { cancel, ok, _status });

        Controls.Add(_canvas);
        Controls.Add(_hint);
        Controls.Add(bar);
        Controls.Add(bottom);

        _btnRect.Click += (_, _) => SelectTool(Tool.Rectangle);
        _btnEllipse.Click += (_, _) => SelectTool(Tool.Ellipse);
        _btnEraser.Click += (_, _) => SelectTool(Tool.Eraser);
        _btnUndo.Click += (_, _) => UndoStep();
        _btnReset.Click += (_, _) => { _history.Clear(); SetCurrent(_original, remember: false); };

        SelectTool(Tool.Ellipse);
        SetCurrent(png, remember: false);
    }

    /// <summary>Итоговая картинка (PNG).</summary>
    public byte[] Result => _current;

    private Tool CurrentTool { get; set; }
    private int EraserRadius => EraserSizes[Math.Max(0, _eraserSize.SelectedIndex)].Radius;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z)) { UndoStep(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SelectTool(Tool tool)
    {
        CurrentTool = tool;
        _btnRect.Checked = tool == Tool.Rectangle;
        _btnEllipse.Checked = tool == Tool.Ellipse;
        _btnEraser.Checked = tool == Tool.Eraser;
        _eraserSize.Enabled = tool == Tool.Eraser;
        _hint.Text = tool switch
        {
            Tool.Rectangle => "Обведите мышкой нужную часть — всё снаружи удалится.",
            Tool.Ellipse => "Обведите круглую печать — всё вокруг круга удалится.",
            _ => "Водите мышкой с нажатой кнопкой по лишнему — оно сотрётся."
        };
        _canvas.Cursor = tool == Tool.Eraser ? Cursors.Hand : Cursors.Cross;
    }

    private void UndoStep()
    {
        if (_history.Count > 0) SetCurrent(_history.Pop(), remember: false);
    }

    /// <summary>Применяет правку; ошибка (например, «ничего не останется») — в строке состояния.</summary>
    private void Apply(Func<byte[], byte[]> edit)
    {
        try
        {
            SetCurrent(edit(_current), remember: true);
        }
        catch (PdfToolException ex)
        {
            _status.Text = ex.Message.Replace('\n', ' ');
        }
    }

    private void SetCurrent(byte[] png, bool remember)
    {
        if (remember) _history.Push(_current);
        _current = png;
        _status.Text = "";
        _btnUndo.Enabled = _history.Count > 0;
        _btnReset.Enabled = _history.Count > 0 || !ReferenceEquals(_current, _original);
        using var ms = new MemoryStream(png);
        using var decoded = new Bitmap(ms);
        _canvas.SetImage(new Bitmap(decoded));
    }

    /// <summary>Картинка на «шахматке», выделение рамкой/кругом и след ластика.</summary>
    private sealed class EditCanvas : Control
    {
        private const int ViewMargin = 20;
        private readonly StampEditForm _owner;
        private readonly TextureBrush _checker;
        private Bitmap? _image;
        private Point? _dragStart;
        private Point _dragNow;
        private readonly List<Point> _stroke = new();

        public EditCanvas(StampEditForm owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(70, 70, 70);
            using var tile = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(tile))
            {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.Gainsboro, 0, 0, 8, 8);
                g.FillRectangle(Brushes.Gainsboro, 8, 8, 8, 8);
            }
            _checker = new TextureBrush(tile);
        }

        public void SetImage(Bitmap image)
        {
            _image?.Dispose();
            _image = image;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _image?.Dispose();
                _checker.Dispose();
            }
            base.Dispose(disposing);
        }

        private RectangleF ImageArea()
        {
            if (_image == null) return RectangleF.Empty;
            var scale = Math.Min((float)(ClientSize.Width - 2 * ViewMargin) / _image.Width,
                (float)(ClientSize.Height - 2 * ViewMargin) / _image.Height);
            scale = Math.Max(0.01f, scale);
            float w = _image.Width * scale, h = _image.Height * scale;
            return new RectangleF((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        private float ViewScale => _image == null ? 1 : ImageArea().Width / _image.Width;

        private (float X, float Y) ToImage(Point p)
        {
            var area = ImageArea();
            return ((p.X - area.X) / ViewScale, (p.Y - area.Y) / ViewScale);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_image == null) return;
            var g = e.Graphics;
            var area = ImageArea();
            g.FillRectangle(_checker, area);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_image, area);

            if (_dragStart is { } start && _owner.CurrentTool != Tool.Eraser)
            {
                var sel = Selection(start, _dragNow);
                using var dim = new Region(area);
                if (_owner.CurrentTool == Tool.Ellipse)
                {
                    using var path = new GraphicsPath();
                    path.AddEllipse(sel);
                    dim.Exclude(path);
                }
                else dim.Exclude(sel);
                using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRegion(shade, dim);
                using var pen = new Pen(Color.DodgerBlue, 2) { DashStyle = DashStyle.Dash };
                if (_owner.CurrentTool == Tool.Ellipse) g.DrawEllipse(pen, sel);
                else g.DrawRectangle(pen, sel);
            }

            if (_stroke.Count > 0)
            {
                var d = _owner.EraserRadius * 2;
                using var brush = new SolidBrush(Color.FromArgb(140, 220, 50, 50));
                foreach (var p in _stroke) g.FillEllipse(brush, p.X - d / 2f, p.Y - d / 2f, d, d);
            }
        }

        private static Rectangle Selection(Point a, Point b) =>
            Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_image == null || e.Button != MouseButtons.Left) return;
            Capture = true;
            if (_owner.CurrentTool == Tool.Eraser) _stroke.Add(e.Location);
            else
            {
                _dragStart = e.Location;
                _dragNow = e.Location;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_owner.CurrentTool == Tool.Eraser && _stroke.Count > 0) _stroke.Add(e.Location);
            else if (_dragStart != null) _dragNow = e.Location;
            else return;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            if (_image == null) return;

            if (_stroke.Count > 0)
            {
                var points = _stroke.Select(ToImage).ToList();
                var radius = _owner.EraserRadius / ViewScale;
                _stroke.Clear();
                _owner.Apply(png => StampImage.Erase(png, points, radius));
            }
            else if (_dragStart is { } start)
            {
                _dragStart = null;
                var sel = Selection(start, e.Location);
                var (x, y) = ToImage(sel.Location);
                var w = sel.Width / ViewScale;
                var h = sel.Height / ViewScale;
                var shape = _owner.CurrentTool == Tool.Ellipse ? CropShape.Ellipse : CropShape.Rectangle;
                if (sel.Width > 4 && sel.Height > 4)
                    _owner.Apply(png => StampImage.Crop(png, (int)x, (int)y, (int)Math.Ceiling(w), (int)Math.Ceiling(h), shape));
            }
            Invalidate();
        }
    }
}
