namespace PdfTools.Core;

/// <summary>Правка страниц документа: перестановка, поворот, удаление, сохранение.</summary>
public static class PdfPageEditor
{
    /// <summary>
    /// Переносит страницы с указанными индексами так, чтобы они встали перед
    /// страницей <paramref name="insertBefore"/> (индекс в текущем порядке, 0..PageCount).
    /// Возвращает новые индексы перенесённых страниц (для восстановления выделения).
    /// </summary>
    public static IReadOnlyList<int> Move(PdfDocumentModel model, IEnumerable<int> indices, int insertBefore)
    {
        var selected = model.NormalizeIndices(indices);
        if (insertBefore < 0 || insertBefore > model.PageCount)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Недопустимая позиция для вставки.");

        var selectedSet = selected.ToHashSet();
        var moving = selected.Select(i => model.Pages[i]).ToList();
        var rest = model.Pages.Where((_, i) => !selectedSet.Contains(i)).ToList();

        // Позиция вставки в списке без перемещаемых страниц
        var target = insertBefore - selected.Count(i => i < insertBefore);
        var result = new List<PageRef>(rest);
        result.InsertRange(target, moving);

        var newIndices = Enumerable.Range(target, moving.Count).ToList();
        if (!result.SequenceEqual(model.Pages))
            model.ReplacePages(result);
        return newIndices;
    }

    /// <summary>Поворачивает страницы на <paramref name="degrees"/> (кратно 90; +90 — по часовой).</summary>
    public static void Rotate(PdfDocumentModel model, IEnumerable<int> indices, int degrees)
    {
        if (degrees % 90 != 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Поворот возможен только на угол, кратный 90°.");

        var selected = model.NormalizeIndices(indices).ToHashSet();
        var result = model.Pages
            .Select((p, i) => selected.Contains(i)
                ? p with { Rotation = PdfOutput.NormalizeRotation(p.Rotation + degrees) }
                : p)
            .ToList();
        model.ReplacePages(result);
    }

    /// <summary>Удаляет страницы. Удалить все страницы нельзя.</summary>
    public static void Delete(PdfDocumentModel model, IEnumerable<int> indices)
    {
        var selected = model.NormalizeIndices(indices).ToHashSet();
        if (selected.Count == model.PageCount)
            throw new PdfToolException(PdfErrorKind.InvalidInput,
                "Нельзя удалить все страницы документа — должна остаться хотя бы одна.");

        var result = model.Pages.Where((_, i) => !selected.Contains(i)).ToList();
        model.ReplacePages(result);
    }

    /// <summary>
    /// Ставит подпись/печать на страницы. <paramref name="visibleRect"/> — место на странице
    /// так, как её видит пользователь (в долях 0..1); на каждой странице подпись встанет туда же.
    /// </summary>
    public static void AddStamp(PdfDocumentModel model, IEnumerable<int> indices, int imageId, PageRect visibleRect)
    {
        if (!model.HasStampImage(imageId))
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Картинка подписи не найдена.");
        if (visibleRect.Width <= 0 || visibleRect.Height <= 0 || visibleRect.X < -0.001 || visibleRect.Y < -0.001
            || visibleRect.X + visibleRect.Width > 1.001 || visibleRect.Y + visibleRect.Height > 1.001)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Подпись должна целиком помещаться на странице.");

        var selected = model.NormalizeIndices(indices).ToHashSet();
        var result = model.Pages
            .Select((p, i) => selected.Contains(i)
                ? p with { Stamps = p.Stamps.Append(StampGeometry.Create(imageId, visibleRect, model.VisibleRotation(p))).ToList() }
                : p)
            .ToList();
        model.ReplacePages(result);
    }

    /// <summary>Убирает все подписи и печати с выбранных страниц.</summary>
    public static void RemoveStamps(PdfDocumentModel model, IEnumerable<int> indices)
    {
        var selected = model.NormalizeIndices(indices).ToHashSet();
        if (!selected.Any(i => model.Pages[i].Stamps.Count > 0))
            throw new PdfToolException(PdfErrorKind.InvalidInput, "На выбранных страницах нет подписей.");

        var result = model.Pages
            .Select((p, i) => selected.Contains(i) ? p with { Stamps = Array.Empty<Stamp>() } : p)
            .ToList();
        model.ReplacePages(result);
    }

    /// <summary>
    /// Сохраняет документ в <paramref name="path"/>. Подтверждение перезаписи
    /// исходника — задача UI (см. <see cref="PdfDocumentModel.IsSourceFile"/>).
    /// </summary>
    public static void Save(PdfDocumentModel model, string path)
    {
        using (var output = new PdfOutput(model))
        {
            output.Write(model.Pages, path);
        }
        model.MarkSaved(path);
    }
}
