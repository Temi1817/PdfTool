using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace PdfTools.Core;

/// <summary>
/// Минимальный писатель .docx (Open XML SDK): абзацы с форматированием,
/// картинки и разрывы страниц. Лист A4, поля 2 см.
/// </summary>
internal sealed class DocxWriter : IDisposable
{
    /// <summary>Ширина и высота области текста в пунктах (A4 минус поля по 2 см).</summary>
    public const double ContentWidthPt = 481.9;
    public const double ContentHeightPt = 728.5;

    private const long EmuPerPoint = 12700;

    private readonly WordprocessingDocument _doc;
    private readonly MainDocumentPart _main;
    private readonly Body _body = new();
    private uint _nextImageId = 1;

    public DocxWriter(Stream stream)
    {
        _doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        _main = _doc.AddMainDocumentPart();
        _main.Document = new Document(_body);
    }

    /// <param name="colorHex">Цвет вида "C0392B" или null для чёрного.</param>
    public void AddParagraph(string text, double fontSizePt, bool bold, string? colorHex)
    {
        text = CleanText(text);
        if (text.Length == 0) return;

        // Порядок элементов в RunProperties важен для схемы: b → color → sz
        var props = new RunProperties();
        if (bold) props.Append(new Bold());
        if (colorHex != null) props.Append(new Color { Val = colorHex });
        var halfPoints = (int)Math.Round(Math.Clamp(fontSizePt, 6, 200) * 2);
        props.Append(new FontSize { Val = halfPoints.ToString() });

        _body.Append(new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "120" }),
            new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    /// <summary>Вставляет картинку отдельным абзацем, уменьшая её до размеров листа.</summary>
    public void AddImage(byte[] data, bool isJpeg, double widthPt, double heightPt)
    {
        if (widthPt <= 0 || heightPt <= 0) return;
        var scale = Math.Min(1.0, Math.Min(ContentWidthPt / widthPt, ContentHeightPt / heightPt));
        var cx = (long)(widthPt * scale * EmuPerPoint);
        var cy = (long)(heightPt * scale * EmuPerPoint);

        var part = _main.AddImagePart(isJpeg ? ImagePartType.Jpeg : ImagePartType.Png);
        using (var ms = new MemoryStream(data)) part.FeedData(ms);
        var relId = _main.GetIdOfPart(part);
        var id = _nextImageId++;

        var drawing = new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = $"Рисунок {id}" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"image{id}" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U
        });

        _body.Append(new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "120" }),
            new Run(drawing)));
    }

    public void AddPageBreak() =>
        _body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

    /// <summary>Завершает документ: параметры листа A4 и запись на диск.</summary>
    public void Dispose()
    {
        _body.Append(new SectionProperties(
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1134, Right = 1134U, Bottom = 1134, Left = 1134U, Header = 709U, Footer = 709U, Gutter = 0U }));
        _main.Document!.Save();
        _doc.Dispose();
    }

    // Управляющие символы недопустимы в XML — Word откажется открыть такой файл
    private static string CleanText(string text) =>
        new string(text.Where(c => c == '\t' || (c >= ' ' && c != '￾' && c != '￿')).ToArray()).Trim();
}
