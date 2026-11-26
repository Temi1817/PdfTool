using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfTools.Core.Tests;

/// <summary>
/// Генерация тестовых PDF. Страницы различаются шириной (в пунктах),
/// по ней проверяем, что порядок страниц после операций правильный.
/// </summary>
internal static class TestPdf
{
    public static string Create(string dir, string name, params double[] widths)
    {
        var path = Path.Combine(dir, name);
        using var doc = new PdfDocument();
        foreach (var w in widths)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(w);
            page.Height = XUnit.FromPoint(800);
        }
        doc.Save(path);
        return path;
    }

    /// <summary>Создаёт PDF из N страниц шириной 101, 102, 103 …</summary>
    public static string CreateNumbered(string dir, string name, int count) =>
        Create(dir, name, Enumerable.Range(1, count).Select(i => 100.0 + i).ToArray());

    public static double[] ReadWidths(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => Math.Round(p.MediaBox.Width)).ToArray();
    }

    public static int[] ReadRotations(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => p.Rotate).ToArray();
    }
}

/// <summary>Временная папка, удаляется после теста.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PdfToolTests_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* не мешаем тестам */ }
    }
}
