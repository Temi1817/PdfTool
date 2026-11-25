using PdfSharp.Pdf;
using SkiaSharp;

namespace PdfTools.Core.Tests;

public class PdfOpenAndRenderTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Open_reads_all_pages()
    {
        var model = PdfDocumentModel.Open(TestPdf.CreateNumbered(_tmp.Path, "Мой файл.pdf", 7));

        Assert.Equal(7, model.PageCount);
        Assert.Equal("Мой файл", model.BaseName);
        Assert.False(model.IsModified);
        Assert.All(model.Pages, p => Assert.Equal(0, p.Rotation));
    }

    [Fact]
    public void Open_missing_file_reports_not_found()
    {
        var ex = Assert.Throws<PdfToolException>(() =>
            PdfDocumentModel.Open(Path.Combine(_tmp.Path, "нет.pdf")));

        Assert.Equal(PdfErrorKind.NotFound, ex.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("это не pdf")]
    [InlineData("%PDF-1.4\n обрыв")]
    public void Open_broken_file_reports_corrupted(string content)
    {
        var path = Path.Combine(_tmp.Path, "broken.pdf");
        File.WriteAllText(path, content);

        var ex = Assert.Throws<PdfToolException>(() => PdfDocumentModel.Open(path));

        Assert.Equal(PdfErrorKind.Corrupted, ex.Kind);
        Assert.Contains("broken.pdf", ex.Message);
    }

    [Fact]
    public void Open_password_protected_file_reports_password()
    {
        var path = Path.Combine(_tmp.Path, "secret.pdf");
        using (var doc = new PdfDocument())
        {
            doc.AddPage();
            doc.SecuritySettings.UserPassword = "123";
            doc.SecuritySettings.OwnerPassword = "456";
            doc.Save(path);
        }

        var ex = Assert.Throws<PdfToolException>(() => PdfDocumentModel.Open(path));

        Assert.Equal(PdfErrorKind.PasswordProtected, ex.Kind);
        Assert.Contains("парол", ex.Message);
    }

    [Fact]
    public void Open_file_locked_exclusively_reports_locked()
    {
        var path = TestPdf.CreateNumbered(_tmp.Path, "locked.pdf", 1);

        using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var ex = Assert.Throws<PdfToolException>(() => PdfDocumentModel.Open(path));

        Assert.Equal(PdfErrorKind.FileLocked, ex.Kind);
        Assert.Contains("занят", ex.Message);
    }

    [Fact]
    public void Open_file_held_open_with_sharing_works()
    {
        var path = TestPdf.CreateNumbered(_tmp.Path, "shared.pdf", 2);

        // Так держат файл многие просмотрщики
        using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(2, PdfDocumentModel.Open(path).PageCount);
    }

    [Theory]
    [InlineData(0, 120, 160)]
    [InlineData(90, 120, 160)]
    public void Render_returns_png_of_requested_box_size(int rotation, int width, int height)
    {
        var model = PdfDocumentModel.Open(TestPdf.Create(_tmp.Path, "r.pdf", 595));
        var page = model.Pages[0] with { Rotation = rotation };

        var png = PdfRenderer.RenderThumbnailPng(model, page, width, height);

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        Assert.Equal(width, bmp.Width);
        Assert.Equal(height, bmp.Height);
        // В центре миниатюры — белая страница, а не прозрачный фон
        var center = bmp.GetPixel(width / 2, height / 2);
        Assert.Equal(255, center.Alpha);
        Assert.True(center.Red > 240 && center.Green > 240 && center.Blue > 240);
    }

    [Fact]
    public void Landscape_page_keeps_aspect_ratio()
    {
        // Страница 1000x800 → в рамке 120x160 должна занять 120x96, а не растянуться
        var path = Path.Combine(_tmp.Path, "wide.pdf");
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage();
            p.Width = PdfSharp.Drawing.XUnit.FromPoint(1000);
            p.Height = PdfSharp.Drawing.XUnit.FromPoint(800);
            doc.Save(path);
        }
        var model = PdfDocumentModel.Open(path);

        using var bmp = SKBitmap.Decode(PdfRenderer.RenderThumbnailPng(model, model.Pages[0], 120, 160));

        Assert.Equal(0, bmp.GetPixel(60, 20).Alpha);   // поле сверху
        Assert.Equal(0, bmp.GetPixel(60, 140).Alpha);  // поле снизу
        Assert.Equal(255, bmp.GetPixel(60, 80).Alpha); // страница
        Assert.Equal(255, bmp.GetPixel(3, 80).Alpha);  // во всю ширину
    }

    [Fact]
    public void Rotated_landscape_thumbnail_fits_into_box()
    {
        // Страница 595x800 (портрет), повёрнутая на 90° → альбомная; должна вписаться по ширине
        var model = PdfDocumentModel.Open(TestPdf.Create(_tmp.Path, "land.pdf", 595));
        var page = model.Pages[0] with { Rotation = 90 };

        var png = PdfRenderer.RenderThumbnailPng(model, page, 120, 160);

        using var bmp = SKBitmap.Decode(png);
        Assert.Equal(0, bmp.GetPixel(60, 2).Alpha);   // сверху — пустое поле
        Assert.Equal(255, bmp.GetPixel(60, 80).Alpha); // по центру — страница
    }
}
