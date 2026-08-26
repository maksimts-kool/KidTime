namespace KidTime.Domain.Localization;

/// <summary>
/// The language every message the controlled user sees is written in - Windows notifications,
/// the tray dashboard, the countdown card, and the removal flow. The parent picks it per device
/// and it travels inside the rule snapshot, so changing it bumps the rule revision and reaches
/// the PC exactly like any other rule change.
/// </summary>
public enum AgentLanguage
{
    English = 0,
    Russian = 1
}
