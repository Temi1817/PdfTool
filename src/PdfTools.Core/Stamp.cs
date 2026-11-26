using System.Globalization;

namespace PdfTools.Core;

/// <summary>Прямоугольник в долях страницы (0..1), начало — левый верхний угол, Y вниз.</summary>
public readonly record struct PageRect(double X, double Y, double Width, double Height);

/// <summary>
/// Подпись или печать на странице. Координаты — доли неповёрнутой страницы (как она лежит в файле),
/// поэтому при повороте страницы подпись поворачивается вместе с ней, как на бумаге.
/// <paramref name="Rotation"/> — поворот самой картинки (по часовой), чтобы при установке она смотрела прямо.
/// Картинка вписывается в прямоугольник с сохранением пропорций.
/// </summary>
public sealed record Stamp(int ImageId, PageRect Rect, int Rotation)
{
    public string Key => string.Create(CultureInfo.InvariantCulture,
        $"{ImageId}@{Rect.X:F4},{Rect.Y:F4},{Rect.Width:F4},{Rect.Height:F4}r{Rotation}");
}

/// <summary>Перевод координат между видимой (повёрнутой) и неповёрнутой страницей.</summary>
public static class StampGeometry
{
    /// <summary>Прямоугольник неповёрнутой страницы → как он выглядит при повороте страницы на rotation (по часовой).</summary>
    public static PageRect ToVisible(PageRect r, int rotation) => PdfOutput.NormalizeRotation(rotation) switch
    {
        90 => new PageRect(1 - (r.Y + r.Height), r.X, r.Height, r.Width),
        180 => new PageRect(1 - (r.X + r.Width), 1 - (r.Y + r.Height), r.Width, r.Height),
        270 => new PageRect(r.Y, 1 - (r.X + r.Width), r.Height, r.Width),
        _ => r
    };

    /// <summary>Обратное преобразование: видимый прямоугольник → неповёрнутая страница.</summary>
    public static PageRect FromVisible(PageRect r, int rotation) => ToVisible(r, 360 - PdfOutput.NormalizeRotation(rotation));

    /// <summary>Подпись, поставленная на видимую страницу, повёрнутую на rotation, — так, чтобы смотрела прямо.</summary>
    public static Stamp Create(int imageId, PageRect visibleRect, int rotation) =>
        new(imageId, FromVisible(visibleRect, rotation), PdfOutput.NormalizeRotation(360 - rotation));

    /// <summary>Вписывает картинку (ширина × высота с учётом её поворота) в прямоугольник по центру.</summary>
    internal static (double X, double Y, double W, double H) Fit(double x, double y, double w, double h,
        double imageW, double imageH, int imageRotation)
    {
        if (imageRotation is 90 or 270) (imageW, imageH) = (imageH, imageW);
        var scale = Math.Min(w / imageW, h / imageH);
        var fw = imageW * scale;
        var fh = imageH * scale;
        return (x + (w - fw) / 2, y + (h - fh) / 2, fw, fh);
    }
}
