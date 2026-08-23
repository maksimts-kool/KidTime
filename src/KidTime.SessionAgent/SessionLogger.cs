using System.Text.Json;

namespace KidTime.SessionAgent;

internal static class SessionLogger
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KidTime", "logs", "session-agent.ndjson");

    public static void Information(string message, Exception? exception = null)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            var line = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level = exception is null ? "Information" : "Error",
                message,
                exception = exception?.ToString()
            });
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
    }
}
