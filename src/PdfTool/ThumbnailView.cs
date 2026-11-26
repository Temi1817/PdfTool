using System.Runtime.InteropServices;
using PdfTools.Core;

namespace PdfTool;

/// <summary>
/// Сетка миниатюр страниц на базе ListView: выделение (клик / Ctrl / Shift),
/// перетаскивание для перестановки, фоновый рендер миниатюр.
/// Сама документ не меняет — сообщает форме через события.
/// </summary>
internal sealed class ThumbnailView : ListView
{
    private const string LoadingKey = "__loading";
    private const string ErrorKey = "__error";
    private const string DragFormat = "PdfTool.PageIndices";

    private readonly Size _thumbSize;
    private readonly ImageList _images = new();
    private readonly HashSet<string> _failed = new();
    // Какие элементы списка показывают миниатюру с данным ключом
    private readonly Dictionary<string, List<ListViewItem>> _itemsByKey = new();
    private PdfDocumentModel? _model;
    private CancellationTokenSource? _renderCts;

    /// <summary>Пользователь перетащил страницы: (выбранные индексы, вставить перед индексом).</summary>
    public event Action<IReadOnlyList<int>, int>? MoveRequested;

    /// <summary>В окно бросили файлы.</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>Прогресс рендера: (готово, всего). При завершении готово == всего.</summary>
    public event Action<int, int>? RenderProgress;

    /// <summary>Двойной клик (или Enter) по странице — открыть её крупно.</summary>
    public event Action<int>? PageOpenRequested;

    public ThumbnailView()
    {
        View = View.LargeIcon;
        MultiSelect = true;
        HideSelection = false;
        AllowDrop = true;
        AutoArrange = true;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;

        var scale = DeviceDpi / 96f;
        _thumbSize = new Size((int)(120 * scale), (int)(160 * scale));
        _images.ColorDepth = ColorDepth.Depth32Bit;
        _images.ImageSize = _thumbSize;
        // Создаём handle сразу: после этого ImageList копирует картинки к себе,
        // и наши Bitmap можно освобождать сразу после добавления
        _ = _images.Handle;
        AddPlaceholders();
        LargeImageList = _images;
    }

    public IReadOnlyList<int> SelectedPageIndices =>
        SelectedIndices.Cast<int>().Order().ToList();

    /// <summary>Показывает новый документ (или очищает, если null).</summary>
    public void SetDocument(PdfDocumentModel? model)
    {
        CancelRendering();
        _model = model;
        _failed.Clear();
        _images.Images.Clear();
        AddPlaceholders();
        RefreshPages();
    }

    /// <summary>Перестраивает список по текущему состоянию модели и дорисовывает недостающие миниатюры.</summary>
    public void RefreshPages(IEnumerable<int>? selectIndices = null)
    {
        BeginUpdate();
        try
        {
            Items.Clear();
            _itemsByKey.Clear();
            if (_model != null)
            {
                var items = new ListViewItem[_model.PageCount];
                for (var i = 0; i < items.Length; i++)
                {
                    var page = _model.Pages[i];
                    var key = KeyOf(page);
                    items[i] = new ListViewItem((i + 1).ToString()) { Tag = page, ImageKey = ImageKeyFor(key) };
                    if (!_itemsByKey.TryGetValue(key, out var list))
                        _itemsByKey[key] = list = new List<ListViewItem>();
                    list.Add(items[i]);
                }
                Items.AddRange(items);
            }

            if (selectIndices != null)
            {
                foreach (var i in selectIndices.Where(i => i >= 0 && i < Items.Count))
                    Items[i].Selected = true;
                if (SelectedIndices.Count > 0)
                {
                    var first = SelectedIndices[0];
                    Items[first].Focused = true;
                    EnsureVisible(first);
                }
            }
        }
        finally
        {
            EndUpdate();
        }
        StartRendering();
    }

    public void SelectAllPages()
    {
        BeginUpdate();
        foreach (ListViewItem item in Items) item.Selected = true;
        EndUpdate();
    }

    public void CancelRendering()
    {
        _renderCts?.Cancel();
        _renderCts = null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Расстояние между ячейками сетки: миниатюра + поля + строка с номером
        var cx = _thumbSize.Width + LogicalToDeviceUnits(28);
        var cy = _thumbSize.Height + LogicalToDeviceUnits(36);
        SendMessage(Handle, LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)((cy << 16) | (cx & 0xFFFF)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelRendering();
            _images.Dispose();
        }
        base.Dispose(disposing);
    }

    // ───────────── Фоновый рендер ─────────────

    private static string KeyOf(PageRef p) => p.RenderKey; // учитывает поворот и подписи

    private string ImageKeyFor(string key) =>
        _images.Images.ContainsKey(key) ? key : _failed.Contains(key) ? ErrorKey : LoadingKey;

    /// <summary>
    /// Рендерит недостающие миниатюры по одной в фоне. Цикл выполняется в UI-потоке,
    /// в фон уходит только сам рендер — поэтому ImageList и элементы трогаем безопасно.
    /// </summary>
    private async void StartRendering()
    {
        CancelRendering();
        var model = _model;
        if (model == null) return;

        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var token = cts.Token;

        var pending = model.Pages
            .DistinctBy(KeyOf)
            .Where(p => ImageKeyFor(KeyOf(p)) == LoadingKey)
            .ToList();
        var total = pending.Count;
        var done = 0;
        RenderProgress?.Invoke(0, total);

        foreach (var page in pending)
        {
            if (token.IsCancellationRequested) return;

            Bitmap? bitmap = null;
            try
            {
                var size = _thumbSize;
                var png = await Task.Run(() => PdfRenderer.RenderThumbnailPng(model, page, size.Width, size.Height), token);
                bitmap = FromPng(png);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Страницу не удалось отрисовать — покажем значок ошибки, остальные продолжаем
            }

            if (token.IsCancellationRequested || IsDisposed)
            {
                bitmap?.Dispose();
                return;
            }

            var key = KeyOf(page);
            if (bitmap != null)
            {
                _images.Images.Add(key, bitmap);
                bitmap.Dispose();
            }
            else
            {
                _failed.Add(key);
            }

            if (_itemsByKey.TryGetValue(key, out var items))
            {
                var imageKey = ImageKeyFor(key);
                foreach (var item in items) item.ImageKey = imageKey;
            }

            done++;
            RenderProgress?.Invoke(done, total);
        }
    }

    private static Bitmap FromPng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var decoded = new Bitmap(ms);
        return new Bitmap(decoded); // копия, не зависящая от потока
    }

    private void AddPlaceholders()
    {
        _images.Images.Add(LoadingKey, DrawPlaceholder("…", Color.FromArgb(245, 245, 245)));
        _images.Images.Add(ErrorKey, DrawPlaceholder("Ошибка\nотрисовки", Color.FromArgb(255, 235, 235)));
    }

    private Bitmap DrawPlaceholder(string text, Color fill)
    {
        var bmp = new Bitmap(_thumbSize.Width, _thumbSize.Height);
        using var g = Graphics.FromImage(bmp);
        var w = (int)(_thumbSize.Width * 0.75);
        var rect = new Rectangle((_thumbSize.Width - w) / 2, 0, w, _thumbSize.Height - 1);
        using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, rect);
        g.DrawRectangle(Pens.Silver, rect);
        TextRenderer.DrawText(g, text, Font, rect, Color.Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        return bmp;
    }

    protected override void OnItemActivate(EventArgs e)
    {
        base.OnItemActivate(e);
        if (FocusedItem != null) PageOpenRequested?.Invoke(FocusedItem.Index);
    }

    // ───────────── Перетаскивание ─────────────

    protected override void OnItemDrag(ItemDragEventArgs e)
    {
        base.OnItemDrag(e);
        if (SelectedIndices.Count == 0) return;
        DoDragDrop(new DataObject(DragFormat, SelectedPageIndices.ToArray()), DragDropEffects.Move);
        InsertionMark.Index = -1;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        UpdateDragState(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDragState(e);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        InsertionMark.Index = -1;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            FilesDropped?.Invoke(files);
            return;
        }

        if (e.Data?.GetData(DragFormat) is not int[] indices) return;

        var mark = InsertionMark.Index;
        var insertBefore = mark < 0 ? Items.Count : mark + (InsertionMark.AppearsAfterItem ? 1 : 0);
        InsertionMark.Index = -1;
        MoveRequested?.Invoke(indices, insertBefore);
    }

    private void UpdateDragState(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }
        if (e.Data?.GetDataPresent(DragFormat) != true)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Move;
        var pt = PointToClient(new Point(e.X, e.Y));
        AutoScrollWhileDragging(pt);

        var index = InsertionMark.NearestIndex(pt);
        if (index >= 0)
        {
            var bounds = GetItemRect(index);
            InsertionMark.AppearsAfterItem = pt.X > bounds.Left + bounds.Width / 2;
        }
        InsertionMark.Index = index;
    }

    // Прокрутка, когда при перетаскивании курсор у верхнего или нижнего края
    private void AutoScrollWhileDragging(Point pt)
    {
        var zone = LogicalToDeviceUnits(40);
        var step = _thumbSize.Height / 3;
        if (pt.Y < zone)
            SendMessage(Handle, LVM_SCROLL, IntPtr.Zero, -step);
        else if (pt.Y > ClientSize.Height - zone)
            SendMessage(Handle, LVM_SCROLL, IntPtr.Zero, step);
    }

    private const int LVM_SCROLL = 0x1014;
    private const int LVM_SETICONSPACING = 0x1035;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
