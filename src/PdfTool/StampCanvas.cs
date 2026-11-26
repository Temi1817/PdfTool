using System.Drawing.Drawing2D;
using PdfTools.Core;

namespace PdfTool;

/// <summary>
/// Страница с подписью поверх неё. Подпись перетаскивается мышью,
/// размер меняется за уголок справа внизу (пропорции сохраняются).
/// Положение — в долях видимой страницы (<see cref="StampRect"/>).
/// </summary>
internal sealed class StampCanvas : Control
{
    private const int PageMargin = 16;
    private const int HandleSize = 12;
    private const double MinWidth = 0.03;

    private Bitmap? _page;
    private Bitmap? _stamp;
    private PageRect _rect = new(0.6, 0.8, 0.28, 0.1);
    private enum DragMode { None, Move, Resize }
    private DragMode _drag;
    private PointF _dragOffset;

    public StampCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(70, 70, 70);
    }

    public PageRect StampRect => _rect;

    public void SetPage(Bitmap page)
    {
        _page?.Dispose();
        _page = page;
        FixAspect(keepCenterBottomRight: true);
        Invalidate();
    }

    /// <summary>Новая картинка подписи: ширина и место сохраняются, высота — по пропорциям картинки.</summary>
    public void SetStamp(Bitmap? stamp)
    {
        _stamp?.Dispose();
        _stamp = stamp;
        FixAspect(keepCenterBottomRight: false);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _page?.Dispose();
            _stamp?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ───────────── Геометрия ─────────────

    /// <summary>Где на экране нарисована страница.</summary>
    private RectangleF PageArea()
    {
        if (_page == null) return RectangleF.Empty;
        var w = ClientSize.Width - 2 * PageMargin;
        var h = ClientSize.Height - 2 * PageMargin;
        if (w <= 0 || h <= 0) return RectangleF.Empty;
        var scale = Math.Min((float)w / _page.Width, (float)h / _page.Height);
        var pw = _page.Width * scale;
        var ph = _page.Height * scale;
        return new RectangleF((ClientSize.Width - pw) / 2, (ClientSize.Height - ph) / 2, pw, ph);
    }

    private RectangleF StampArea(RectangleF page) => new(
        page.X + (float)(_rect.X * page.Width), page.Y + (float)(_rect.Y * page.Height),
        (float)(_rect.Width * page.Width), (float)(_rect.Height * page.Height));

    /// <summary>Высота рамки в долях страницы, при которой рамка повторяет пропорции картинки.</summary>
    private double HeightFor(double width)
    {
        if (_stamp == null || _page == null) return _rect.Height;
        return width * _page.Width / _page.Height * _stamp.Height / _stamp.Width;
    }

    private void FixAspect(bool keepCenterBottomRight)
    {
        if (_stamp == null || _page == null) return;
        var w = Math.Clamp(_rect.Width, MinWidth, 1);
        var h = HeightFor(w);
        if (h > 0.9) { h = 0.9; w = h / HeightFor(1); }
        var x = keepCenterBottomRight ? 1 - w - 0.06 : _rect.X;
        var y = keepCenterBottomRight ? 1 - h - 0.08 : _rect.Y;
        _rect = Clamp(new PageRect(x, y, w, h));
    }

    private static PageRect Clamp(PageRect r) => r with
    {
        X = Math.Clamp(r.X, 0, Math.Max(0, 1 - r.Width)),
        Y = Math.Clamp(r.Y, 0, Math.Max(0, 1 - r.Height))
    };

    // ───────────── Рисование ─────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var page = PageArea();
        if (_page == null || page.IsEmpty)
        {
            TextRenderer.DrawText(g, "Загрузка…", Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_page, page);
        if (_stamp == null) return;

        var area = StampArea(page);
        g.DrawImage(_stamp, area);
        using var pen = new Pen(Color.DodgerBlue, 1.5f) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, area.X, area.Y, area.Width, area.Height);
        g.FillRectangle(Brushes.DodgerBlue, ResizeGrip(area));
    }

    private static RectangleF ResizeGrip(RectangleF area) =>
        new(area.Right - HandleSize / 2f, area.Bottom - HandleSize / 2f, HandleSize, HandleSize);

    // ───────────── Мышь ─────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var page = PageArea();
        if (_stamp == null || page.IsEmpty || e.Button != MouseButtons.Left) return;

        var area = StampArea(page);
        var handle = ResizeGrip(area);
        handle.Inflate(4, 4);
        if (handle.Contains(e.Location)) _drag = DragMode.Resize;
        else if (area.Contains(e.Location))
        {
            _drag = DragMode.Move;
            _dragOffset = new PointF(e.X - area.X, e.Y - area.Y);
        }
        if (_drag != DragMode.None) Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var page = PageArea();
        if (_stamp == null || page.IsEmpty) return;

        switch (_drag)
        {
            case DragMode.Move:
                _rect = Clamp(_rect with
                {
                    X = (e.X - _dragOffset.X - page.X) / page.Width,
                    Y = (e.Y - _dragOffset.Y - page.Y) / page.Height
                });
                Invalidate();
                break;
            case DragMode.Resize:
                var w = Math.Clamp((e.X - page.X) / page.Width - _rect.X, MinWidth, 1 - _rect.X);
                var h = HeightFor(w);
                if (_rect.Y + h > 1) { h = 1 - _rect.Y; w = h / HeightFor(1); }
                _rect = _rect with { Width = w, Height = h };
                Invalidate();
                break;
            default:
                var area = StampArea(page);
                var handle = ResizeGrip(area);
                handle.Inflate(4, 4);
                Cursor = handle.Contains(e.Location) ? Cursors.SizeNWSE
                    : area.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = DragMode.None;
        Capture = false;
    }
}
