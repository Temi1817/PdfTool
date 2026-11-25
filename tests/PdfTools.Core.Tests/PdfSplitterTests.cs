namespace PdfTools.Core.Tests;

public class PdfSplitterTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void SplitByRanges_creates_file_per_range_with_expected_names()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "Отчёт.pdf", 10);
        var model = PdfDocumentModel.Open(src);
        var ranges = PageRangeParser.Parse("1-3, 4-7, 8-10", model.PageCount);
        var outDir = Directory.CreateDirectory(Path.Combine(_tmp.Path, "out")).FullName;

        var files = PdfSplitter.SplitByRanges(model, ranges, outDir, model.BaseName);

        Assert.Equal(new[] { "Отчёт_1-3.pdf", "Отчёт_4-7.pdf", "Отчёт_8-10.pdf" },
            files.Select(Path.GetFileName));
        Assert.Equal(new double[] { 101, 102, 103 }, TestPdf.ReadWidths(files[0]));
        Assert.Equal(new double[] { 104, 105, 106, 107 }, TestPdf.ReadWidths(files[1]));
        Assert.Equal(new double[] { 108, 109, 110 }, TestPdf.ReadWidths(files[2]));
    }

    [Fact]
    public void PlanRangeFiles_matches_actual_output()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 5);
        var model = PdfDocumentModel.Open(src);
        var ranges = PageRangeParser.Parse("1-2, 5", 5);

        var planned = PdfSplitter.PlanRangeFiles(_tmp.Path, "doc", ranges);
        var created = PdfSplitter.SplitByRanges(model, ranges, _tmp.Path, "doc");

        Assert.Equal(planned, created);
    }

    [Fact]
    public void SplitEachPage_creates_one_file_per_page()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 4);
        var model = PdfDocumentModel.Open(src);
        var outDir = Directory.CreateDirectory(Path.Combine(_tmp.Path, "each")).FullName;

        var files = PdfSplitter.SplitEachPage(model, outDir, "doc");

        Assert.Equal(new[] { "doc_1.pdf", "doc_2.pdf", "doc_3.pdf", "doc_4.pdf" }, files.Select(Path.GetFileName));
        for (var i = 0; i < 4; i++)
            Assert.Equal(new double[] { 101 + i }, TestPdf.ReadWidths(files[i]));
    }

    [Fact]
    public void Split_uses_current_order_and_rotation_from_editor()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 4);
        var model = PdfDocumentModel.Open(src);
        PdfPageEditor.Move(model, new[] { 3 }, 0);       // 4,1,2,3
        PdfPageEditor.Rotate(model, new[] { 0 }, 90);

        var files = PdfSplitter.SplitByRanges(model, new[] { new PageRange(1, 2) }, _tmp.Path, "x");

        Assert.Equal(new double[] { 104, 101 }, TestPdf.ReadWidths(files[0]));
        Assert.Equal(new[] { 90, 0 }, TestPdf.ReadRotations(files[0]));
    }

    [Fact]
    public void ExtractPages_puts_selected_pages_into_one_file()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 6);
        var model = PdfDocumentModel.Open(src);
        var outPath = Path.Combine(_tmp.Path, "selected.pdf");

        PdfSplitter.ExtractPages(model, new[] { 4, 1, 1, 2 }, outPath);

        Assert.Equal(new double[] { 102, 103, 105 }, TestPdf.ReadWidths(outPath));
    }

    [Fact]
    public void Split_does_not_change_model()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 3);
        var model = PdfDocumentModel.Open(src);

        PdfSplitter.SplitEachPage(model, _tmp.Path, "doc");

        Assert.False(model.IsModified);
        Assert.False(model.CanUndo);
        Assert.Equal(src, model.FilePath);
    }

    [Fact]
    public void Split_into_missing_folder_gives_clear_error()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 3);
        var model = PdfDocumentModel.Open(src);

        var ex = Assert.Throws<PdfToolException>(() =>
            PdfSplitter.SplitEachPage(model, Path.Combine(_tmp.Path, "нет_такой"), "doc"));

        Assert.Equal(PdfErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void Range_outside_document_is_rejected()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 3);
        var model = PdfDocumentModel.Open(src);

        Assert.Throws<PdfToolException>(() =>
            PdfSplitter.SplitByRanges(model, new[] { new PageRange(2, 5) }, _tmp.Path, "doc"));
    }

    [Fact]
    public void Splitting_over_locked_output_file_reports_locked()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "doc.pdf", 2);
        var model = PdfDocumentModel.Open(src);
        var target = Path.Combine(_tmp.Path, "doc_1.pdf");
        File.WriteAllText(target, "занят");

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<PdfToolException>(() =>
                PdfSplitter.SplitByRanges(model, new[] { new PageRange(1, 1) }, _tmp.Path, "doc"));
            Assert.Equal(PdfErrorKind.FileLocked, ex.Kind);
        }

        // Временных файлов не осталось
        Assert.Empty(Directory.GetFiles(_tmp.Path, "*.tmp"));
    }
}
