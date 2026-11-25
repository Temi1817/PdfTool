namespace PdfTools.Core.Tests;

public class PdfPageEditorTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private PdfDocumentModel OpenNumbered(int count) =>
        PdfDocumentModel.Open(TestPdf.CreateNumbered(_tmp.Path, $"doc{Guid.NewGuid():N}.pdf", count));

    private string SaveAndGetPath(PdfDocumentModel model)
    {
        var path = Path.Combine(_tmp.Path, $"out{Guid.NewGuid():N}.pdf");
        PdfPageEditor.Save(model, path);
        return path;
    }

    [Theory]
    [InlineData(new[] { 0 }, 3, new double[] { 102, 103, 101, 104, 105 })]  // первую — перед 4-й
    [InlineData(new[] { 4 }, 0, new double[] { 105, 101, 102, 103, 104 })]  // последнюю — в начало
    [InlineData(new[] { 0, 2 }, 5, new double[] { 102, 104, 105, 101, 103 })] // две — в конец
    [InlineData(new[] { 1, 3 }, 0, new double[] { 102, 104, 101, 103, 105 })] // разрозненные — в начало
    public void Move_reorders_pages(int[] selected, int insertBefore, double[] expected)
    {
        var model = OpenNumbered(5);

        PdfPageEditor.Move(model, selected, insertBefore);

        Assert.Equal(expected, TestPdf.ReadWidths(SaveAndGetPath(model)));
    }

    [Fact]
    public void Move_returns_new_positions_of_moved_pages()
    {
        var model = OpenNumbered(5);

        var newIndices = PdfPageEditor.Move(model, new[] { 0, 1 }, 4);

        Assert.Equal(new[] { 2, 3 }, newIndices);
    }

    [Fact]
    public void Move_to_same_place_does_not_create_undo_step()
    {
        var model = OpenNumbered(3);

        PdfPageEditor.Move(model, new[] { 1 }, 1);
        PdfPageEditor.Move(model, new[] { 1 }, 2);

        Assert.False(model.CanUndo);
        Assert.False(model.IsModified);
    }

    [Fact]
    public void Rotate_accumulates_and_wraps_around()
    {
        var model = OpenNumbered(3);

        PdfPageEditor.Rotate(model, new[] { 0, 2 }, 90);
        PdfPageEditor.Rotate(model, new[] { 0 }, 90);
        PdfPageEditor.Rotate(model, new[] { 2 }, -180);

        Assert.Equal(new[] { 180, 0, 270 }, TestPdf.ReadRotations(SaveAndGetPath(model)));
    }

    [Fact]
    public void Rotate_adds_to_existing_page_rotation()
    {
        var path = TestPdf.CreateNumbered(_tmp.Path, "rot.pdf", 1);
        var rotated = Path.Combine(_tmp.Path, "rot90.pdf");
        var model = PdfDocumentModel.Open(path);
        PdfPageEditor.Rotate(model, new[] { 0 }, 90);
        PdfPageEditor.Save(model, rotated);

        var model2 = PdfDocumentModel.Open(rotated);
        PdfPageEditor.Rotate(model2, new[] { 0 }, 90);

        Assert.Equal(new[] { 180 }, TestPdf.ReadRotations(SaveAndGetPath(model2)));
    }

    [Fact]
    public void Rotate_by_non_right_angle_is_rejected()
    {
        var model = OpenNumbered(2);

        Assert.Throws<PdfToolException>(() => PdfPageEditor.Rotate(model, new[] { 0 }, 45));
    }

    [Fact]
    public void Delete_removes_pages()
    {
        var model = OpenNumbered(5);

        PdfPageEditor.Delete(model, new[] { 1, 3 });

        Assert.Equal(new double[] { 101, 103, 105 }, TestPdf.ReadWidths(SaveAndGetPath(model)));
    }

    [Fact]
    public void Delete_all_pages_is_rejected()
    {
        var model = OpenNumbered(2);

        var ex = Assert.Throws<PdfToolException>(() => PdfPageEditor.Delete(model, new[] { 0, 1 }));

        Assert.Equal(PdfErrorKind.InvalidInput, ex.Kind);
        Assert.Equal(2, model.PageCount);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 3 })]
    public void Invalid_indices_are_rejected(int[] indices)
    {
        var model = OpenNumbered(3);

        Assert.Throws<PdfToolException>(() => PdfPageEditor.Delete(model, indices));
        Assert.Throws<PdfToolException>(() => PdfPageEditor.Rotate(model, indices, 90));
    }

    [Fact]
    public void Undo_reverts_steps_in_reverse_order()
    {
        var model = OpenNumbered(4);
        var original = model.Pages.ToList();

        PdfPageEditor.Rotate(model, new[] { 0 }, 90);
        var afterRotate = model.Pages.ToList();
        PdfPageEditor.Delete(model, new[] { 1 });
        PdfPageEditor.Move(model, new[] { 0 }, 3);

        Assert.True(model.Undo());
        Assert.True(model.Undo());
        Assert.Equal(afterRotate, model.Pages);
        Assert.True(model.Undo());
        Assert.Equal(original, model.Pages);
        Assert.False(model.Undo());
    }

    [Fact]
    public void Save_marks_model_as_saved_and_switches_path()
    {
        var model = OpenNumbered(2);
        PdfPageEditor.Rotate(model, new[] { 0 }, 90);

        var path = SaveAndGetPath(model);

        Assert.False(model.IsModified);
        Assert.Equal(path, model.FilePath);
    }

    [Fact]
    public void Can_overwrite_source_file_because_it_is_not_locked()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "src.pdf", 3);
        var model = PdfDocumentModel.Open(src);
        Assert.True(model.IsSourceFile(src));

        PdfPageEditor.Delete(model, new[] { 0 });
        PdfPageEditor.Save(model, src);

        Assert.Equal(new double[] { 102, 103 }, TestPdf.ReadWidths(src));
        Assert.Empty(Directory.GetFiles(_tmp.Path, "*.tmp"));
    }

    [Fact]
    public void Source_file_is_not_locked_after_open()
    {
        var src = TestPdf.CreateNumbered(_tmp.Path, "free.pdf", 2);
        var model = PdfDocumentModel.Open(src);

        // Если бы файл держался открытым, эксклюзивное открытие упало бы
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(src);

        // Модель продолжает работать на данных из памяти
        Assert.Equal(new double[] { 101, 102 }, TestPdf.ReadWidths(SaveAndGetPath(model)));
    }
}
