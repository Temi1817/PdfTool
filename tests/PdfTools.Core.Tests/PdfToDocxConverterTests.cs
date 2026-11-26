using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PdfTools.Core.Tests;

/// <summary>Конвертация PDF → Word без Microsoft Word.</summary>
public class PdfToDocxConverterTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string Docx(string name = "out.docx") => Path.Combine(_tmp.Path, name);

    private static string BodyText(string docx)
    {
        using var doc = WordprocessingDocument.Open(docx, false);
        return string.Join("\n", doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => p.InnerText));
    }

    [Fact]
    public void Text_becomes_editable_paragraphs()
    {
        var model = PdfDocumentModel.Open(TestPdf.CreateMixed(_tmp.Path, "t.pdf", "text"));

        var result = PdfToDocxConverter.Convert(model, Docx());

        Assert.Equal(1, result.PageCount);
        Assert.Equal(0, result.PagesAsImages);
        Assert.Contains("Привет, текст", BodyText(Docx()));
    }

    [Fact]
    public void Heading_keeps_size_bold_and_color()
    {
        var model = PdfDocumentModel.Open(TestPdf.CreateMixed(_tmp.Path, "h.pdf", "heading"));

        PdfToDocxConverter.Convert(model, Docx());

        using var doc = WordprocessingDocument.Open(Docx(), false);
        var runs = doc.MainDocumentPart!.Document!.Body!.Descendants<Run>().ToList();
        var heading = runs.Single(r => r.InnerText == "Заголовок").RunProperties!;
        Assert.NotNull(heading.Bold);
        Assert.Equal("60", heading.FontSize!.Val!.Value);      // 30pt = 60 полупунктов
        Assert.Equal("FF0000", heading.Color!.Val!.Value);

        var normal = runs.Single(r => r.InnerText == "Обычный текст").RunProperties!;
        Assert.Null(normal.Bold);
        Assert.Null(normal.Color);                              // чёрный не пишем
        Assert.Equal("24", normal.FontSize!.Val!.Value);
    }

    [Fact]
    public void Images_and_scans_are_kept_blank_pages_are_skipped()
    {
        // стр. 1 текст, 2 пустая, 3 скан (только картинка), 4 текст + картинка
        var model = PdfDocumentModel.Open(
            TestPdf.CreateMixed(_tmp.Path, "m.pdf", "text", "blank", "image", "text+image"));

        var result = PdfToDocxConverter.Convert(model, Docx());

        Assert.Equal(4, result.PageCount);
        Assert.Equal(1, result.PagesAsImages);
        using var doc = WordprocessingDocument.Open(Docx(), false);
        Assert.Equal(2, doc.MainDocumentPart!.ImageParts.Count());   // скан стр. 3 + картинка стр. 4
        var pageBreaks = doc.MainDocumentPart.Document!.Body!.Descendants<Break>()
            .Count(b => b.Type?.Value == BreakValues.Page);
        Assert.Equal(3, pageBreaks);
    }

    [Fact]
    public void Uses_current_page_order_from_editor()
    {
        var model = PdfDocumentModel.Open(
            TestPdf.CreateMixed(_tmp.Path, "o.pdf", "word:Первая", "word:Вторая", "word:Третья"));
        PdfPageEditor.Move(model, new[] { 2 }, 0);
        PdfPageEditor.Delete(model, new[] { 2 });

        PdfToDocxConverter.Convert(model, Docx());

        var text = BodyText(Docx());
        Assert.True(text.IndexOf("Третья", StringComparison.Ordinal) < text.IndexOf("Первая", StringComparison.Ordinal));
        Assert.DoesNotContain("Вторая", text);
    }

    [Theory]
    [InlineData(new[] { "Привет,", "мир" }, "Привет, мир")]
    [InlineData(new[] { "пере-", "нос слова" }, "перенос слова")]
    [InlineData(new[] { "Алма-", "Ата" }, "Алма- Ата")]      // после дефиса заглавная — не перенос
    [InlineData(new[] { "  a ", "", "b" }, "a b")]
    public void JoinLines_merges_wrapped_lines(string[] lines, string expected)
    {
        Assert.Equal(expected, PdfToDocxConverter.JoinLines(lines));
    }

    [Fact]
    public void Existing_file_is_replaced_and_no_temp_files_left()
    {
        File.WriteAllText(Docx(), "старое");
        var model = PdfDocumentModel.Open(TestPdf.CreateMixed(_tmp.Path, "t.pdf", "text"));

        PdfToDocxConverter.Convert(model, Docx());

        Assert.Contains("Привет", BodyText(Docx()));
        Assert.Empty(Directory.GetFiles(_tmp.Path, "*.tmp"));
    }

    [Fact]
    public void Locked_target_reports_clear_error()
    {
        File.WriteAllText(Docx(), "занят");
        var model = PdfDocumentModel.Open(TestPdf.CreateMixed(_tmp.Path, "t.pdf", "text"));

        using (new FileStream(Docx(), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<PdfToolException>(() => PdfToDocxConverter.Convert(model, Docx()));
            Assert.Equal(PdfErrorKind.FileLocked, ex.Kind);
        }
        Assert.Empty(Directory.GetFiles(_tmp.Path, "*.tmp"));
    }
}
