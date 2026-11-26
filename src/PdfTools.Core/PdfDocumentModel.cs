namespace PdfTools.Core;

/// <summary>
/// Ссылка на одну страницу: из какого исходника, какая по счёту (с 0)
/// и на сколько градусов её дополнительно повернули (0/90/180/270, по часовой).
/// </summary>
public sealed record PageRef(int SourceId, int PageIndex, int Rotation = 0)
{
    /// <summary>Подписи и печати, наложенные на страницу (попадают в файл при сохранении).</summary>
    public IReadOnlyList<Stamp> Stamps { get; init; } = Array.Empty<Stamp>();

    /// <summary>Ключ внешнего вида страницы — для кэша миниатюр.</summary>
    public string RenderKey => Stamps.Count == 0
        ? $"{SourceId}:{PageIndex}:{Rotation}"
        : $"{SourceId}:{PageIndex}:{Rotation}:{string.Join(";", Stamps.Select(s => s.Key))}";
}

/// <summary>
/// Текущий документ в редакторе — упорядоченный список страниц из одного
/// или нескольких исходных PDF. Реальный PDF собирается только при сохранении.
/// </summary>
public sealed class PdfDocumentModel
{
    private const int MaxUndo = 100;

    // Доступ к исходникам под замком: фоновый рендер читает их, пока UI добавляет новые
    private readonly object _sync = new();
    private readonly Dictionary<int, PdfSource> _sources = new();
    // Картинки подписей/печатей (PNG). Не удаляются — на них могут ссылаться шаги отмены
    private readonly Dictionary<int, byte[]> _stampImages = new();
    private readonly List<List<PageRef>> _undo = new();
    private List<PageRef> _pages = new();
    private int _nextSourceId = 1;
    private int _nextStampImageId = 1;

    /// <summary>Путь, куда документ сохраняется по «Сохранить». Изначально — открытый файл.</summary>
    public string FilePath { get; private set; }

    /// <summary>Имя файла без расширения — основа для имён при делении.</summary>
    public string BaseName => Path.GetFileNameWithoutExtension(FilePath);

    public IReadOnlyList<PageRef> Pages => _pages;
    public int PageCount => _pages.Count;
    public bool IsModified { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public IReadOnlyCollection<PdfSource> Sources
    {
        get { lock (_sync) return _sources.Values.ToList(); }
    }

    private PdfDocumentModel(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>Открывает PDF. Бросает PdfToolException с понятным текстом.</summary>
    public static PdfDocumentModel Open(string path)
    {
        var model = new PdfDocumentModel(path);
        var source = model.AddSource(path);
        model._pages = Enumerable.Range(0, source.PageCount)
            .Select(i => new PageRef(source.Id, i))
            .ToList();
        return model;
    }

    public PdfSource GetSource(int id)
    {
        lock (_sync)
        {
            return _sources.TryGetValue(id, out var s)
                ? s
                : throw new PdfToolException(PdfErrorKind.InvalidInput, $"Неизвестный исходный файл (id={id}).");
        }
    }

    /// <summary>Запоминает картинку подписи (PNG) и возвращает её номер для <see cref="Stamp"/>.</summary>
    public int AddStampImage(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Пустая картинка подписи.");
        lock (_sync)
        {
            var id = _nextStampImageId++;
            _stampImages.Add(id, png);
            return id;
        }
    }

    internal byte[] GetStampImage(int id)
    {
        lock (_sync)
        {
            return _stampImages.TryGetValue(id, out var png)
                ? png
                : throw new PdfToolException(PdfErrorKind.InvalidInput, $"Неизвестная картинка подписи (id={id}).");
        }
    }

    internal bool HasStampImage(int id)
    {
        lock (_sync) return _stampImages.ContainsKey(id);
    }

    /// <summary>Как страница видна читателю: её собственный поворот в файле + поворот в редакторе.</summary>
    public int VisibleRotation(PageRef page) =>
        PdfOutput.NormalizeRotation(GetSource(page.SourceId).GetPageRotation(page.PageIndex) + page.Rotation);

    /// <summary>Совпадает ли путь с одним из исходных файлов (для подтверждения перезаписи).</summary>
    public bool IsSourceFile(string path)
    {
        var full = Path.GetFullPath(path);
        return Sources.Any(s => string.Equals(s.FilePath, full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Отменяет последнее изменение списка страниц.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _pages = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        IsModified = true;
        return true;
    }

    internal PdfSource AddSource(string path)
    {
        int id;
        lock (_sync) id = _nextSourceId++;
        var source = PdfSource.Load(id, path); // долгая загрузка — вне замка
        lock (_sync) _sources.Add(source.Id, source);
        return source;
    }

    /// <summary>Заменяет список страниц, запоминая старый для отмены.</summary>
    internal void ReplacePages(List<PageRef> newPages)
    {
        _undo.Add(_pages);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _pages = newPages;
        IsModified = true;
    }

    internal void MarkSaved(string path)
    {
        FilePath = Path.GetFullPath(path);
        IsModified = false;
    }

    /// <summary>Проверяет индексы страниц и возвращает их без повторов по возрастанию.</summary>
    internal List<int> NormalizeIndices(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var list = indices.Distinct().Order().ToList();
        if (list.Count == 0)
            throw new PdfToolException(PdfErrorKind.InvalidInput, "Не выбрано ни одной страницы.");
        if (list[0] < 0 || list[^1] >= _pages.Count)
            throw new PdfToolException(PdfErrorKind.InvalidInput,
                $"Номер страницы вне диапазона (в документе {_pages.Count} стр.).");
        return list;
    }
}
