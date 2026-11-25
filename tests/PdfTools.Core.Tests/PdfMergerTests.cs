namespace PdfTools.Core.Tests;

public class PdfMergerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Append_adds_pages_to_end_and_is_undoable()
    {
        var a = TestPdf.Create(_tmp.Path, "a.pdf", 101, 102);
        var b = TestPdf.Create(_tmp.Path, "b.pdf", 201, 202, 203);
        var model = PdfDocumentModel.Open(a);

        var added = PdfMerger.Append(model, b);

        Assert.Equal(3, added);
        Assert.Equal(5, model.PageCount);
        Assert.True(model.IsModified);

        var outPath = Path.Combine(_tmp.Path, "merged.pdf");
        PdfPageEditor.Save(model, outPath);
        Assert.Equal(new double[] { 101, 102, 201, 202, 203 }, TestPdf.ReadWidths(outPath));

        Assert.True(model.Undo());
        Assert.Equal(2, model.PageCount);
    }

    [Fact]
    public void Appended_pages_can_be_reordered_with_original_ones()
    {
        var a = TestPdf.Create(_tmp.Path, "a.pdf", 101, 102);
        var b = TestPdf.Create(_tmp.Path, "b.pdf", 201);
        var model = PdfDocumentModel.Open(a);
        PdfMerger.Append(model, b);

        PdfPageEditor.Move(model, new[] { 2 }, 1);
        var outPath = Path.Combine(_tmp.Path, "out.pdf");
        PdfPageEditor.Save(model, outPath);

        Assert.Equal(new double[] { 101, 201, 102 }, TestPdf.ReadWidths(outPath));
    }

    [Fact]
    public void Same_file_can_be_appended_twice()
    {
        var a = TestPdf.Create(_tmp.Path, "a.pdf", 101, 102);
        var model = PdfDocumentModel.Open(a);
        PdfMerger.Append(model, a);

        var outPath = Path.Combine(_tmp.Path, "twice.pdf");
        PdfPageEditor.Save(model, outPath);

        Assert.Equal(new double[] { 101, 102, 101, 102 }, TestPdf.ReadWidths(outPath));
    }

    [Fact]
    public void MergeFiles_keeps_given_order()
    {
        var a = TestPdf.Create(_tmp.Path, "a.pdf", 101);
        var b = TestPdf.Create(_tmp.Path, "b.pdf", 201, 202);
        var c = TestPdf.Create(_tmp.Path, "c.pdf", 301);
        var outPath = Path.Combine(_tmp.Path, "all.pdf");

        PdfMerger.MergeFiles(new[] { c, a, b }, outPath);

        Assert.Equal(new double[] { 301, 101, 201, 202 }, TestPdf.ReadWidths(outPath));
    }

    [Fact]
    public void MergeFiles_without_inputs_is_rejected()
    {
        Assert.Throws<PdfToolException>(() =>
            PdfMerger.MergeFiles(Array.Empty<string>(), Path.Combine(_tmp.Path, "x.pdf")));
    }

    [Fact]
    public void Append_of_broken_file_leaves_model_unchanged()
    {
        var a = TestPdf.Create(_tmp.Path, "a.pdf", 101);
        var broken = Path.Combine(_tmp.Path, "broken.pdf");
        File.WriteAllText(broken, "это не pdf");
        var model = PdfDocumentModel.Open(a);

        var ex = Assert.Throws<PdfToolException>(() => PdfMerger.Append(model, broken));

        Assert.Equal(PdfErrorKind.Corrupted, ex.Kind);
        Assert.Equal(1, model.PageCount);
        Assert.False(model.CanUndo);
    }
}
