namespace KidTime.SessionAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        SessionLogger.Information("SessionAgent started.");
        Application.Run(new AgentApplicationContext());
    }
}
