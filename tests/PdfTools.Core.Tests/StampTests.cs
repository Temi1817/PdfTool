using SkiaSharp;

namespace PdfTools.Core.Tests;

/// <summary>Подпись и печать: подготовка картинки, место на странице, сохранение, повороты, отмена.</summary>
public class StampTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>«Фото подписи»: синий прямоугольник на белой бумаге.</summary>
    private static byte[] BlueInkOnPaper(byte paper = 255)
    {
        using var bmp = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(paper, paper, paper));
            using var ink = new SKPaint { Color = new SKColor(20, 40, 200) };
            canvas.DrawRect(100, 70, 100, 60, ink);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static bool IsBlue(SKColor c) => c.Blue > 150 && c.Red < 110 && c.Green < 130;

    /// <summary>Доля синих пикселей в области страницы (доли 0..1).</summary>
    private static double BlueShare(SKBitmap bmp, double x0, double y0, double x1, double y1)
    {
        int blue = 0, all = 0;
        for (var y = (int)(bmp.Height * y0); y < (int)(bmp.Height * y1); y++)
        for (var x = (int)(bmp.Width * x0); x < (int)(bmp.Width * x1); x++)
        {
            all++;
            if (IsBlue(bmp.GetPixel(x, y))) blue++;
        }
        return all == 0 ? 0 : (double)blue / all;
    }

    private static SKBitmap Render(PdfDocumentModel model, int page = 0) =>
        SKBitmap.Decode(PdfRenderer.RenderPageImage(model, model.Pages[page], 40, jpeg: false));

    private (PdfDocumentModel Model, int ImageId) ModelWithStampImage(int pages = 1)
    {
        var model = PdfDocumentModel.Open(TestPdf.Create(_tmp.Path, $"d{Guid.NewGuid():N}.pdf",
            Enumerable.Repeat(595.0, pages).ToArray()));
        return (model, model.AddStampImage(StampImage.Prepare(BlueInkOnPaper(), removeBackground: true)));
    }

    // Правый нижний угол видимой страницы
    private static readonly PageRect BottomRight = new(0.6, 0.75, 0.3, 0.15);

    // ───────────── Геометрия ─────────────

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Visible_and_unrotated_coordinates_round_trip(int rotation)
    {
        var r = new PageRect(0.1, 0.2, 0.3, 0.15);

        var back = StampGeometry.ToVisible(StampGeometry.FromVisible(r, rotation), rotation);

        Assert.Equal(r.X, back.X, 6);
        Assert.Equal(r.Y, back.Y, 6);
        Assert.Equal(r.Width, back.Width, 6);
        Assert.Equal(r.Height, back.Height, 6);
    }

    // ───────────── Подготовка картинки ─────────────

    [Theory]
    [InlineData(255)]   // белая бумага
    [InlineData(205)]   // сероватая бумага на фото
    public void Paper_becomes_transparent_ink_stays_and_margins_are_trimmed(byte paper)
    {
        var png = StampImage.Prepare(BlueInkOnPaper(paper), removeBackground: true);

        using var bmp = SKBitmap.Decode(png);
        Assert.True(bmp.Width < 130 && bmp.Height < 90, $"обрезано до {bmp.Width}x{bmp.Height}");
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);                       // угол — бумага
        var center = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, center.Alpha);                                 // чернила видны
        Assert.True(IsBlue(center));
    }

    [Fact]
    public void Without_background_removal_picture_is_kept_whole()
    {
        using var bmp = SKBitmap.Decode(StampImage.Prepare(BlueInkOnPaper(), removeBackground: false));

        Assert.Equal(300, bmp.Width);
        Assert.Equal(255, bmp.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void Blank_or_broken_picture_gives_clear_error()
    {
        using var white = new SKBitmap(100, 100);
        white.Erase(SKColors.White);
        using var data = white.Encode(SKEncodedImageFormat.Png, 100);

        Assert.Throws<PdfToolException>(() => StampImage.Prepare(data.ToArray(), removeBackground: true));
        Assert.Throws<PdfToolException>(() => StampImage.Prepare(new byte[] { 1, 2, 3 }, removeBackground: true));
    }

    // ───────────── Ручная правка: обрезка и ластик ─────────────

    /// <summary>Как на фото печати: круг 200×200 в центре и «лишний текст» слева (прозрачный фон).</summary>
    private static byte[] SealWithExtraText()
    {
        using var bmp = new SKBitmap(new SKImageInfo(400, 300, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var ink = new SKPaint { Color = new SKColor(20, 40, 200), IsAntialias = true };
            canvas.DrawCircle(250, 150, 100, ink);     // печать: x 150..350
            canvas.DrawRect(5, 140, 40, 20, ink);      // «тель» слева: x 5..45
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int W, int H) SizeOf(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        return (bmp.Width, bmp.Height);
    }

    [Fact]
    public void Ellipse_crop_keeps_only_round_seal()
    {
        var png = SealWithExtraText();

        var cropped = StampImage.Crop(png, 145, 45, 210, 210, CropShape.Ellipse);

        var (w, h) = SizeOf(cropped);
        Assert.InRange(w, 200, 215);   // только круг (+ небольшой отступ), без «текста» слева
        Assert.InRange(h, 200, 215);
        using var bmp = SKBitmap.Decode(cropped);
        Assert.True(IsBlue(bmp.GetPixel(w / 2, h / 2)));
    }

    [Fact]
    public void Rectangle_crop_keeps_selected_part()
    {
        var cropped = StampImage.Crop(SealWithExtraText(), 0, 130, 60, 40, CropShape.Rectangle);

        var (w, h) = SizeOf(cropped);
        Assert.InRange(w, 40, 50);     // только «текст» 40×20
        Assert.InRange(h, 20, 30);
    }

    [Fact]
    public void Eraser_removes_stray_text()
    {
        var png = SealWithExtraText();
        var before = SizeOf(png);

        var erased = StampImage.Erase(png, new[] { (25f, 130f), (25f, 170f) }, radius: 25);

        var (w, _) = SizeOf(erased);
        Assert.True(w < before.W - 100, $"ширина {w}: «текст» стёрт, осталась печать");
    }

    [Theory]
    [InlineData(10, 10, 2, 2)]       // слишком маленькое выделение
    [InlineData(0, 0, 100, 100)]     // пустой угол — ничего не останется
    public void Bad_crop_gives_clear_error(int x, int y, int w, int h)
    {
        Assert.Throws<PdfToolException>(() => StampImage.Crop(SealWithExtraText(), x, y, w, h, CropShape.Rectangle));
    }

    // ───────────── Место на странице ─────────────

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Stamp_appears_where_placed_in_editor_and_in_saved_file(int pageRotation)
    {
        var (model, id) = ModelWithStampImage();
        if (pageRotation != 0) PdfPageEditor.Rotate(model, new[] { 0 }, pageRotation);

        PdfPageEditor.AddStamp(model, new[] { 0 }, id, BottomRight);

        // В редакторе (миниатюры, просмотр)
        using (var shown = Render(model))
        {
            Assert.True(BlueShare(shown, 0.6, 0.75, 0.9, 0.9) > 0.1, "в редакторе подпись внизу справа");
            Assert.Equal(0, BlueShare(shown, 0, 0, 0.5, 0.5));
        }

        // В сохранённом файле — там же
        var path = Path.Combine(_tmp.Path, $"signed{pageRotation}.pdf");
        PdfPageEditor.Save(model, path);
        var saved = PdfDocumentModel.Open(path);
        using var bmp = Render(saved);
        Assert.True(BlueShare(bmp, 0.6, 0.75, 0.9, 0.9) > 0.1, "в файле подпись внизу справа");
        Assert.Equal(0, BlueShare(bmp, 0, 0, 0.5, 0.5));
    }

    [Fact]
    public void Stamp_turns_with_page_like_on_paper()
    {
        var (model, id) = ModelWithStampImage();
        PdfPageEditor.AddStamp(model, new[] { 0 }, id, BottomRight);

        PdfPageEditor.Rotate(model, new[] { 0 }, 90); // правый нижний угол уходит в левый нижний

        using var bmp = Render(model);
        Assert.True(BlueShare(bmp, 0.05, 0.6, 0.3, 0.95) > 0.05, "после поворота — внизу слева");
        Assert.Equal(0, BlueShare(bmp, 0.5, 0, 1, 1));
    }

    [Fact]
    public void Stamp_on_several_pages_and_undo()
    {
        var (model, id) = ModelWithStampImage(pages: 3);

        PdfPageEditor.AddStamp(model, new[] { 0, 2 }, id, BottomRight);

        Assert.Single(model.Pages[0].Stamps);
        Assert.Empty(model.Pages[1].Stamps);
        Assert.Single(model.Pages[2].Stamps);
        Assert.NotEqual(model.Pages[0].RenderKey, model.Pages[1].RenderKey);

        Assert.True(model.Undo());
        Assert.All(model.Pages, p => Assert.Empty(p.Stamps));
    }

    [Fact]
    public void RemoveStamps_clears_selected_pages_only()
    {
        var (model, id) = ModelWithStampImage(pages: 2);
        PdfPageEditor.AddStamp(model, new[] { 0, 1 }, id, BottomRight);

        PdfPageEditor.RemoveStamps(model, new[] { 1 });

        Assert.Single(model.Pages[0].Stamps);
        Assert.Empty(model.Pages[1].Stamps);
        Assert.Throws<PdfToolException>(() => PdfPageEditor.RemoveStamps(model, new[] { 1 }));
    }

    [Theory]
    [InlineData(0.8, 0.8, 0.3, 0.1)]   // вылезает за правый край
    [InlineData(0.1, 0.1, 0, 0.1)]     // нулевой размер
    [InlineData(-0.2, 0.1, 0.1, 0.1)]  // за левым краем
    public void Stamp_outside_page_is_rejected(double x, double y, double w, double h)
    {
        var (model, id) = ModelWithStampImage();

        Assert.Throws<PdfToolException>(() => PdfPageEditor.AddStamp(model, new[] { 0 }, id, new PageRect(x, y, w, h)));
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void Split_and_numbering_keep_stamps()
    {
        var (model, id) = ModelWithStampImage(pages: 2);
        PdfPageEditor.AddStamp(model, new[] { 1 }, id, BottomRight);

        var files = PdfSplitter.SplitEachPage(model, _tmp.Path, "part");
        var numbered = Path.Combine(_tmp.Path, "num.pdf");
        PdfPageNumberer.AddNumbers(model, numbered, new PageNumberOptions());

        using (var part2 = Render(PdfDocumentModel.Open(files[1])))
            Assert.True(BlueShare(part2, 0.6, 0.75, 0.9, 0.9) > 0.1);
        using (var part1 = Render(PdfDocumentModel.Open(files[0])))
            Assert.Equal(0, BlueShare(part1, 0, 0, 1, 1));
        using (var num = Render(PdfDocumentModel.Open(numbered), page: 1))
            Assert.True(BlueShare(num, 0.6, 0.75, 0.9, 0.9) > 0.1);
    }
}
