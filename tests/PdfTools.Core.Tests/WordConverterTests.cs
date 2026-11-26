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

    [Theory]
    [InlineData("отчёт.docx", true)]
    [InlineData("C:\\папка\\СТАРЫЙ.DOC", true)]
    [InlineData("письмо.rtf", true)]
    [InlineData("шаблон.docm", true)]
    [InlineData("файл.odt", true)]
    [InlineData("скан.pdf", false)]
    [InlineData("таблица.xlsx", false)]
    [InlineData("без_расширения", false)]
    public void IsWordFile_detects_by_extension(string path, bool expected)
    {
        Assert.Equal(expected, WordConverter.IsWordFile(path));
    }

    [WordFact]
    public void Word_document_becomes_pdf_without_leftover_Word()
    {
        var wordBefore = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        var rtf = Path.Combine(_tmp.Path, "Документ.rtf");
        File.WriteAllText(rtf, @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs28 Hello PdfTool\par}");

        var pdf = Path.Combine(_tmp.Path, "Документ.pdf");
        WordConverter.WordToPdf(rtf, pdf);

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
