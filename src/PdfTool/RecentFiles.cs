namespace PdfTool;

/// <summary>
/// Список последних файлов в %AppData%\PdfTool\ (recent.txt — открытые документы,
/// stamps.txt — картинки подписей). Ошибки чтения/записи молча игнорируются — список не критичен.
/// </summary>
internal sealed class RecentFiles
{
    private readonly int _maxCount;
    private readonly string _storePath;
    private readonly List<string> _items = new();

    public RecentFiles(string fileName = "recent.txt", int maxCount = 10)
    {
        _maxCount = maxCount;
        _storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfTool", fileName);
        Load();
    }

    public IReadOnlyList<string> Items => _items;

    public void Add(string path)
    {
        var full = Path.GetFullPath(path);
        _items.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, full);
        if (_items.Count > _maxCount) _items.RemoveRange(_maxCount, _items.Count - _maxCount);
        Save();
    }

    public void Remove(string path)
    {
        _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            _items.AddRange(File.ReadAllLines(_storePath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && Path.IsPathFullyQualified(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(_maxCount));
        }
        catch
        {
            // повреждённый файл списка — начинаем с пустого
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllLines(_storePath, _items);
        }
        catch
        {
            // нет доступа к AppData — просто не запоминаем
        }
    }
}
