using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace KidTime.Setup;

public partial class App : Application
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KidTime", "logs", "setup.ndjson");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Setup runs on the parent's screen before any device exists, so it has no way to send a
        // fault to the server. It says what went wrong and where the detail was written instead
        // of leaving Windows to show an unexplained "unknown software exception".
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Report(args.ExceptionObject as Exception);
        ApplicationThemeManager.ApplySystemTheme();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report(e.Exception);
        if (MainWindow is null) Shutdown(1);
    }

    private static void Report(Exception? exception)
    {
        var detail = exception?.ToString() ?? "No exception detail was available.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level = "Error",
                message = exception?.Message ?? "KidTime Setup failed.",
                exception = detail
            }) + Environment.NewLine);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        MessageBox.Show(
            $"KidTime Setup could not continue.\n\n{exception?.Message}\n\nDetails were written to:\n{LogFile}",
            "KidTime Setup",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
