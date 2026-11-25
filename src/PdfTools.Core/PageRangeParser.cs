using System.Globalization;

namespace PdfTools.Core;

/// <summary>Диапазон страниц, номера с 1 (как видит пользователь), границы включительно.</summary>
public readonly record struct PageRange(int Start, int End)
{
    public int Count => End - Start + 1;

    /// <summary>"5" для одной страницы, "1-3" для диапазона — используется в именах файлов.</summary>
    public override string ToString() => Start == End
        ? Start.ToString(CultureInfo.InvariantCulture)
        : $"{Start}-{End}";
}

/// <summary>Разбор строки вида "1-3, 5, 7-10".</summary>
public static class PageRangeParser
{
    private static readonly char[] Separators = { ',', ';' };
    private static readonly char[] Dashes = { '-', '–', '—' };

    /// <summary>
    /// Разбирает строку диапазонов. Бросает PdfToolException(InvalidInput)
    /// с описанием, что именно не так.
    /// </summary>
    public static IReadOnlyList<PageRange> Parse(string? input, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw Error("Введите диапазоны страниц, например: 1-3, 5, 7-10");
        if (pageCount <= 0)
            throw Error("В документе нет страниц.");

        var result = new List<PageRange>();
        foreach (var rawPart in input.Split(Separators))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue; // лишняя запятая — не ошибка

            var dash = part.IndexOfAny(Dashes);
            PageRange range;
            if (dash < 0)
            {
                var page = ParseNumber(part, part);
                range = new PageRange(page, page);
            }
            else
            {
                var left = part[..dash].Trim();
                var right = part[(dash + 1)..].Trim();
                if (left.Length == 0 || right.Length == 0)
                    throw Error($"Неполный диапазон «{part}». Пример правильной записи: 4-7");
                range = new PageRange(ParseNumber(left, part), ParseNumber(right, part));
                if (range.Start > range.End)
                    throw Error($"В диапазоне «{part}» начало больше конца.");
            }

            if (range.End > pageCount)
                throw Error($"Диапазон «{part}» выходит за пределы документа (страниц: {pageCount}).");
            result.Add(range);
        }

        if (result.Count == 0)
            throw Error("Введите диапазоны страниц, например: 1-3, 5, 7-10");
        return result;
    }

    /// <summary>Вариант без исключений — удобно для подсветки поля ввода.</summary>
    public static bool TryParse(string? input, int pageCount,
        out IReadOnlyList<PageRange> ranges, out string error)
    {
        try
        {
            ranges = Parse(input, pageCount);
            error = "";
            return true;
        }
        catch (PdfToolException ex)
        {
            ranges = Array.Empty<PageRange>();
            error = ex.Message;
            return false;
        }
    }

    private static int ParseNumber(string text, string part)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw Error($"«{part}» — не номер страницы. Используйте цифры, запятые и дефис.");
        if (n < 1)
            throw Error($"Номера страниц начинаются с 1 («{part}»).");
        return n;
    }

    private static PdfToolException Error(string message) => new(PdfErrorKind.InvalidInput, message);
}
