namespace PdfTool;

internal static class Program
{
    /// <summary>Иконка приложения (из exe) — для заголовков всех окон.</summary>
    internal static readonly Icon? AppIcon = LoadIcon();

    private static Icon? LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Любая непойманная ошибка — сообщение пользователю, а не падение
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowFatal(ex);
        };

        Application.Run(new MainForm(args.FirstOrDefault()));
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show($"Непредвиденная ошибка:\n{ex.Message}", "PdfTool",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
