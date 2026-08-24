using System.Windows;
using Wpf.Ui.Appearance;

namespace KidTime.SessionAgent;

public partial class App : Application
{
    private AgentApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
        SessionLogger.Information("SessionAgent started.");
        _host = new AgentApplicationHost();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
