using System.Diagnostics;

namespace PdfTools.Core.Tests;

/// <summary>
/// Тест, запускающий настоящий Microsoft Word. По умолчанию пропускается,
/// чтобы не мешать Word пользователя. Включить: переменная окружения PDFTOOL_WORD_TESTS=1.
/// </summary>
public sealed class WordFactAttribute : FactAttribute
{
    public WordFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PDFTOOL_WORD_TESTS") != "1")
            Skip = "Тест запускает Microsoft Word — включается через PDFTOOL_WORD_TESTS=1";
        else if (!WordConverter.IsWordInstalled())
            Skip = "Microsoft Word не установлен";
    }
}

public class WordConverterTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void IsWordInstalled_does_not_throw()
    {
        _ = WordConverter.IsWordInstalled();
    }

    [Fact]
    public void WordToPdf_missing_file_reports_not_found()
    {
        var ex = Assert.Throws<PdfToolException>(() =>
            WordConverter.WordToPdf(Path.Combine(_tmp.Path, "нет.docx"), Path.Combine(_tmp.Path, "x.pdf")));

        Assert.Equal(PdfErrorKind.NotFound, ex.Kind);
    }

    [WordFact]
    public void Docx_from_our_converter_goes_back_to_pdf_without_leftover_Word()
    {
        var wordBefore = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        var model = PdfDocumentModel.Open(TestPdf.CreateMixed(_tmp.Path, "Документ.pdf", "text", "heading"));
        var docx = Path.Combine(_tmp.Path, "Документ.docx");
        PdfToDocxConverter.Convert(model, docx);

        var pdf = Path.Combine(_tmp.Path, "Обратно.pdf");
        WordConverter.WordToPdf(docx, pdf);

        Assert.True(PdfDocumentModel.Open(pdf).PageCount >= 1);
        Assert.Empty(Directory.GetFiles(_tmp.Path, ".*.tmp*"));
        Assert.All(Process.GetProcessesByName("WINWORD"), p => Assert.Contains(p.Id, wordBefore));
    }

    [WordFact]
    public void WordToPdf_of_broken_file_reports_clear_error()
    {
        var broken = Path.Combine(_tmp.Path, "broken.docx");
        File.WriteAllText(broken, "это не документ word");

        var ex = Assert.Throws<PdfToolException>(() =>
            WordConverter.WordToPdf(broken, Path.Combine(_tmp.Path, "out.pdf")));

        Assert.Equal(PdfErrorKind.ConversionFailed, ex.Kind);
        Assert.False(File.Exists(Path.Combine(_tmp.Path, "out.pdf")));
    }
}
