namespace KidTime.ControlService.Infrastructure;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "https://localhost:5081";
    public string? PinnedServerCertificateSha256 { get; set; }
    public int SyncIntervalSeconds { get; set; } = 60;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int UpdateCheckIntervalSeconds { get; set; } = 300;
}
