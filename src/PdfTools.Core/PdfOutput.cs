using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfTools.Core;

/// <summary>
/// Сборка нового PDF из списка PageRef (с поворотами и подписями) и безопасная запись на диск.
/// Общая для сохранения, деления, склейки, сжатия и нумерации.
/// </summary>
internal sealed class PdfOutput : IDisposable
{
    private readonly PdfDocumentModel _model;
    private readonly Dictionary<int, PdfDocument> _opened = new();
    // Картинки подписей живут до конца операции: документ сохраняется после рисования
    private readonly List<XImage> _stampImages = new();

    public PdfOutput(PdfDocumentModel model)
    {
        _model = model;
    }

    /// <summary>Собирает страницы в новый PDF и записывает его в <paramref name="path"/>.</summary>
    public void Write(IReadOnlyList<PageRef> pages, string path)
    {
        using var output = Build(pages);
        SaveDocument(output, path);
    }

    /// <summary>Безопасно сохраняет готовый документ на диск.</summary>
    internal static void SaveDocument(PdfDocument doc, string path) =>
        WriteAtomically(path, stream => doc.Save(stream, closeStream: false));

    /// <summary>Собирает новый PDF из страниц. Вызывающий обязан сделать Dispose.</summary>
    internal PdfDocument Build(IReadOnlyList<PageRef> pages)
    {
        if (pages.Count == 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Нет страниц для сохранения.");

        var output = new PdfDocument();
        output.Info.Creator = "PdfTool";
        var images = new Dictionary<int, XImage>();
        foreach (var pageRef in pages)
        {
            var source = GetOpened(pageRef.SourceId);
            var page = output.AddPage(source.Pages[pageRef.PageIndex]);
            if (pageRef.Rotation != 0)
                page.Rotate = NormalizeRotation(page.Rotate + pageRef.Rotation);
            if (pageRef.Stamps.Count > 0)
                DrawStamps(page, pageRef.Stamps, images);
        }
        return output;
    }

    /// <summary>
    /// Рисует подписи в координатах неповёрнутой страницы (видимая область — CropBox);
    /// /Rotate страницы потом поворачивает их вместе с содержимым.
    /// </summary>
    private void DrawStamps(PdfPage page, IReadOnlyList<Stamp> stamps, Dictionary<int, XImage> images)
    {
        var media = page.MediaBox;
        var crop = page.CropBox.IsZero ? media : page.CropBox;
        double offX = crop.X1 - media.X1, offY = media.Y2 - crop.Y2;

        using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        foreach (var stamp in stamps)
        {
            if (!images.TryGetValue(stamp.ImageId, out var image))
            {
                image = XImage.FromStream(new MemoryStream(_model.GetStampImage(stamp.ImageId)));
                images.Add(stamp.ImageId, image);
                _stampImages.Add(image);
            }

            var (x, y, w, h) = StampGeometry.Fit(
                offX + stamp.Rect.X * crop.Width, offY + stamp.Rect.Y * crop.Height,
                stamp.Rect.Width * crop.Width, stamp.Rect.Height * crop.Height,
                image.PixelWidth, image.PixelHeight, stamp.Rotation);

            // Поворачиваем вокруг центра; при 90/270 ширина и высота картинки меняются местами
            var sideways = stamp.Rotation is 90 or 270;
            var (drawW, drawH) = sideways ? (h, w) : (w, h);
            var state = g.Save();
            g.TranslateTransform(x + w / 2, y + h / 2);
            if (stamp.Rotation != 0) g.RotateTransform(stamp.Rotation);
            g.DrawImage(image, -drawW / 2, -drawH / 2, drawW, drawH);
            g.Restore(state);
        }
    }

    public void Dispose()
    {
        foreach (var doc in _opened.Values) doc.Dispose();
        _opened.Clear();
        foreach (var image in _stampImages) image.Dispose();
        _stampImages.Clear();
    }

    internal static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360;

    // Исходник открывается один раз на всю операцию (важно при делении на 100+ файлов)
    private PdfDocument GetOpened(int sourceId)
    {
        if (!_opened.TryGetValue(sourceId, out var doc))
        {
            doc = _model.GetSource(sourceId).OpenForImport();
            _opened.Add(sourceId, doc);
        }
        return doc;
    }

    /// <summary>
    /// Пишет во временный файл рядом, потом заменяет целевой.
    /// Если что-то пошло не так — целевой файл остаётся нетронутым.
    /// </summary>
    internal static void WriteAtomically(string path, Action<Stream> write)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(fs);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            // Замена открытого в другой программе файла даёт «Access denied» — уточняем причину
            if (ex is UnauthorizedAccessException && IsLockedByOther(fullPath))
                throw new PdfToolException(PdfErrorKind.FileLocked,
                    $"Файл «{Path.GetFileName(fullPath)}» занят другой программой.\nЗакройте его там и повторите попытку.", ex);
            throw PdfToolException.FromIo(ex, fullPath, writing: true);
        }
    }

    private static bool IsLockedByOther(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* временный файл — не критично */ }
    }
}
