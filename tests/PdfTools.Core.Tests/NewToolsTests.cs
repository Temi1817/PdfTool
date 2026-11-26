using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace PdfTools.Core.Tests;

/// <summary>Картинки → PDF, страницы → картинки, сжатие, нумерация.</summary>
public class NewToolsTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>Картинка с «шумом» — плохо сжимается без потерь, как настоящее фото.</summary>
    private string CreateImage(string name, int w, int h, SKEncodedImageFormat format, int quality = 95)
    {
        var rnd = new Random(42);
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            bmp.SetPixel(x, y, new SKColor((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)rnd.Next(256)));
        using var data = bmp.Encode(format, quality);
        var path = Path.Combine(_tmp.Path, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string PdfWithImage(string imagePath, string name)
    {
        var path = Path.Combine(_tmp.Path, name);
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        using (var img = XImage.FromFile(imagePath))
            g.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point / 2);
        doc.Save(path);
        return path;
    }

    // ───────────── Картинки → PDF ─────────────

    [Fact]
    public void Images_become_pages_with_orientation_of_each_image()
    {
        var portrait = CreateImage("a.jpg", 300, 400, SKEncodedImageFormat.Jpeg);
        var landscape = CreateImage("b.png", 400, 300, SKEncodedImageFormat.Png);
        var webp = CreateImage("c.webp", 200, 200, SKEncodedImageFormat.Webp);
        var pdf = Path.Combine(_tmp.Path, "photos.pdf");

        ImagePdfBuilder.Create(new[] { portrait, landscape, webp }, pdf);

        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        Assert.Equal(3, doc.PageCount);
        Assert.True(doc.Pages[0].MediaBox.Height > doc.Pages[0].MediaBox.Width);
        Assert.True(doc.Pages[1].MediaBox.Width > doc.Pages[1].MediaBox.Height);
        Assert.Empty(Directory.GetFiles(_tmp.Path, "*.tmp"));
    }

    [Fact]
    public void Not_an_image_gives_clear_error()
    {
        var fake = Path.Combine(_tmp.Path, "fake.jpg");
        File.WriteAllText(fake, "это не картинка");

        var ex = Assert.Throws<PdfToolException>(() =>
            ImagePdfBuilder.Create(new[] { fake }, Path.Combine(_tmp.Path, "x.pdf")));

        Assert.Equal(PdfErrorKind.Corrupted, ex.Kind);
        Assert.Contains("fake.jpg", ex.Message);
        Assert.False(File.Exists(Path.Combine(_tmp.Path, "x.pdf")));
    }

    [Theory]
    [InlineData("photo.JPG", true)]
    [InlineData("scan.png", true)]
    [InlineData("pic.webp", true)]
    [InlineData("doc.pdf", false)]
    [InlineData("text.docx", false)]
    public void IsImageFile_detects_by_extension(string path, bool expected)
    {
        Assert.Equal(expected, ImagePdfBuilder.IsImageFile(path));
    }

    [Fact]
    public void Exif_rotation_is_applied()
    {
        // 2x1: левый пиксель красный, правый синий. Поворот на 90° по часовой → 1x2: красный сверху
        using var bmp = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bmp.SetPixel(0, 0, SKColors.Red);
        bmp.SetPixel(1, 0, SKColors.Blue);

        using var rotated = ImagePdfBuilder.ApplyOrigin(bmp, SKEncodedOrigin.RightTop);

        Assert.Equal(1, rotated.Width);
        Assert.Equal(2, rotated.Height);
        Assert.Equal(SKColors.Red, rotated.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, rotated.GetPixel(0, 1));
    }

    // ───────────── Страницы → картинки ─────────────

    [Theory]
    [InlineData(PageImageFormat.Jpeg, ".jpg")]
    [InlineData(PageImageFormat.Png, ".png")]
    public void Pages_are_exported_as_images_with_rotation(PageImageFormat format, string ext)
    {
        var model = PdfDocumentModel.Open(TestPdf.Create(_tmp.Path, "Отчёт.pdf", 595, 595, 595));
        PdfPageEditor.Rotate(model, new[] { 2 }, 90);

        var files = PdfImageExporter.Export(model, new[] { 0, 2 }, _tmp.Path, "Отчёт", format, dpi: 72);

        Assert.Equal(new[] { "Отчёт_стр1" + ext, "Отчёт_стр3" + ext }, files.Select(Path.GetFileName));
        using var first = SKBitmap.Decode(files[0]);
        using var rotated = SKBitmap.Decode(files[1]);
        Assert.Equal(595, first.Width);    // 72 DPI = 1 пиксель на пункт
        Assert.Equal(800, first.Height);
        Assert.Equal(800, rotated.Width);  // повёрнутая — шире, чем выше
        Assert.Equal(595, rotated.Height);
    }

    // ───────────── Сжатие ─────────────

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "noise.png")]   // в PDF хранится без потерь (Flate)
    [InlineData(SKEncodedImageFormat.Jpeg, "noise.jpg")]  // большой JPEG высокого качества
    public void Compression_shrinks_heavy_images(SKEncodedImageFormat format, string name)
    {
        var image = CreateImage(name, 2400, 1600, format);
        var model = PdfDocumentModel.Open(PdfWithImage(image, "heavy.pdf"));
        var outPath = Path.Combine(_tmp.Path, "small.pdf");

        var result = PdfCompressor.Compress(model, outPath);

        Assert.True(result.Saved);
        Assert.Equal(1, result.ImagesRecompressed);
        Assert.True(result.CompressedBytes < result.OriginalBytes / 2,
            $"было {result.OriginalBytes}, стало {result.CompressedBytes}");
        Assert.Equal(result.CompressedBytes, new FileInfo(outPath).Length);
        Assert.Equal(1, PdfDocumentModel.Open(outPath).PageCount);
        // Сжатый файл по-прежнему рисуется
        using var thumb = SKBitmap.Decode(PdfRenderer.RenderThumbnailPng(PdfDocumentModel.Open(outPath),
            PdfDocumentModel.Open(outPath).Pages[0], 120, 160));
        Assert.NotNull(thumb);
    }

    [Fact]
    public void Nothing_to_compress_does_not_write_file()
    {
        var model = PdfDocumentModel.Open(TestPdf.CreateNumbered(_tmp.Path, "empty.pdf", 2));
        var outPath = Path.Combine(_tmp.Path, "out.pdf");

        var result = PdfCompressor.Compress(model, outPath);

        if (!result.Saved) Assert.False(File.Exists(outPath));
        else Assert.True(result.CompressedBytes < result.OriginalBytes);
        Assert.Equal(0, result.ImagesRecompressed);
    }

    // ───────────── Нумерация ─────────────

    /// <summary>Есть ли тёмные пиксели в горизонтальной полосе (доли высоты от..до) центральной трети.</summary>
    private static bool HasInk(SKBitmap bmp, double fromY, double toY, double fromX = 0.3, double toX = 0.7)
    {
        for (var y = (int)(bmp.Height * fromY); y < (int)(bmp.Height * toY); y++)
        for (var x = (int)(bmp.Width * fromX); x < (int)(bmp.Width * toX); x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 200 && c.Red < 120 && c.Green < 120 && c.Blue < 120) return true;
        }
        return false;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Number_appears_at_bottom_center_of_visible_page(int rotation)
    {
        var model = PdfDocumentModel.Open(TestPdf.Create(_tmp.Path, "n.pdf", 595));
        if (rotation != 0) PdfPageEditor.Rotate(model, new[] { 0 }, rotation);
        var outPath = Path.Combine(_tmp.Path, "numbered.pdf");

        PdfPageNumberer.AddNumbers(model, outPath, new PageNumberOptions(FontSize: 40));

        var numbered = PdfDocumentModel.Open(outPath);
        using var bmp = SKBitmap.Decode(PdfRenderer.RenderThumbnailPng(numbered, numbered.Pages[0], 400, 400));
        Assert.True(HasInk(bmp, 0.5, 1.0), "номер должен быть внизу");
        Assert.False(HasInk(bmp, 0.0, 0.5), "вверху номера быть не должно");
    }

    [Fact]
    public void Number_is_drawn_in_chosen_color()
    {
        var model = PdfDocumentModel.Open(TestPdf.Create(_tmp.Path, "c.pdf", 595));
        var outPath = Path.Combine(_tmp.Path, "red.pdf");

        PdfPageNumberer.AddNumbers(model, outPath,
            new PageNumberOptions(Format: "Стр. {n}", FontSize: 60, Color: "#E00000"));

        var numbered = PdfDocumentModel.Open(outPath);
        using var bmp = SKBitmap.Decode(PdfRenderer.RenderThumbnailPng(numbered, numbered.Pages[0], 400, 400));
        int red = 0, black = 0;
        for (var y = bmp.Height / 2; y < bmp.Height - 4; y++)   // нижняя половина, без рамки миниатюры
        for (var x = 4; x < bmp.Width - 4; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha < 200) continue;
            if (c.Red > 180 && c.Green < 80 && c.Blue < 80) red++;
            if (c.Red < 60 && c.Green < 60 && c.Blue < 60) black++;
        }
        Assert.True(red > 50, $"красных пикселей: {red}");
        Assert.Equal(0, black);
    }

    [Theory]
    [InlineData("красный")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Wrong_color_is_rejected(string color)
    {
        var model = PdfDocumentModel.Open(TestPdf.CreateNumbered(_tmp.Path, "w.pdf", 1));
        var outPath = Path.Combine(_tmp.Path, "w_out.pdf");

        Assert.Throws<PdfToolException>(() =>
            PdfPageNumberer.AddNumbers(model, outPath, new PageNumberOptions(Color: color)));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Number_format_and_start_are_validated()
    {
        var model = PdfDocumentModel.Open(TestPdf.CreateNumbered(_tmp.Path, "v.pdf", 2));
        var outPath = Path.Combine(_tmp.Path, "x.pdf");

        Assert.Throws<PdfToolException>(() =>
            PdfPageNumberer.AddNumbers(model, outPath, new PageNumberOptions(Format: "без номера")));
        Assert.Throws<PdfToolException>(() =>
            PdfPageNumberer.AddNumbers(model, outPath, new PageNumberOptions(StartFrom: -1)));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Numbering_keeps_pages_and_does_not_change_model()
    {
        var model = PdfDocumentModel.Open(TestPdf.CreateNumbered(_tmp.Path, "k.pdf", 3));
        var outPath = Path.Combine(_tmp.Path, "k_num.pdf");

        PdfPageNumberer.AddNumbers(model, outPath,
            new PageNumberOptions(NumberPosition.TopRight, "Стр. {n} из {total}", StartFrom: 5));

        Assert.Equal(new double[] { 101, 102, 103 }, TestPdf.ReadWidths(outPath));
        Assert.False(model.IsModified);
    }
}
