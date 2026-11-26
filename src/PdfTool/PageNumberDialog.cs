using PdfTools.Core;

namespace PdfTool;

/// <summary>Настройки нумерации: где ставить номер, в каком виде, с какого числа.</summary>
internal sealed class PageNumberDialog : Form
{
    private static readonly (string Title, NumberPosition Value)[] Positions =
    {
        ("Внизу по центру", NumberPosition.BottomCenter),
        ("Внизу справа", NumberPosition.BottomRight),
        ("Внизу слева", NumberPosition.BottomLeft),
        ("Вверху по центру", NumberPosition.TopCenter),
        ("Вверху справа", NumberPosition.TopRight)
    };

    private static readonly string[] Formats = { "{n}", "Стр. {n}", "{n} из {total}", "Страница {n} из {total}", "- {n} -" };

    private readonly ComboBox _position = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly NumericUpDown _start = new() { Minimum = 0, Maximum = 100_000, Value = 1, Width = 90 };
    private readonly NumericUpDown _size = new() { Minimum = 6, Maximum = 36, Value = 11, Width = 90 };
    private readonly Label _preview = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly int _pageCount;

    public PageNumberDialog(int pageCount)
    {
        _pageCount = pageCount;
        Text = "Нумерация страниц";
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (var (title, _) in Positions) _position.Items.Add(title);
        foreach (var f in Formats) _format.Items.Add(Example(f, 1));
        _position.SelectedIndex = 0;
        _format.SelectedIndex = 0;

        var ok = new Button { Text = "Создать", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12), Dock = DockStyle.Fill };
        AddRow(table, "Где:", _position);
        AddRow(table, "Вид:", _format);
        AddRow(table, "Начать с:", _start);
        AddRow(table, "Размер шрифта:", _size);
        AddRow(table, "Пример:", _preview);
        table.Controls.Add(buttons, 0, table.RowCount);
        table.SetColumnSpan(buttons, 2);
        Controls.Add(table);

        _format.SelectedIndexChanged += (_, _) => UpdatePreview();
        _start.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    public PageNumberOptions Options => new(
        Positions[_position.SelectedIndex].Value,
        Formats[_format.SelectedIndex],
        (int)_start.Value,
        (double)_size.Value);

    private static void AddRow(TableLayoutPanel table, string caption, Control control)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
        control.Margin = new Padding(0, 3, 0, 3);
        table.Controls.Add(control, 1, row);
    }

    private string Example(string format, int n) =>
        format.Replace("{n}", n.ToString()).Replace("{total}", (_pageCount + (int)(_start?.Value ?? 1) - 1).ToString());

    private void UpdatePreview()
    {
        var first = (int)_start.Value;
        _preview.Text = $"{Example(Formats[_format.SelectedIndex], first)}, " +
                        $"{Example(Formats[_format.SelectedIndex], first + 1)}, …";
    }
}
