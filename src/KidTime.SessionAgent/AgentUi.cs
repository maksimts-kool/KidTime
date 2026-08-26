using System.Globalization;
using System.Windows;
using KidTime.Domain.Localization;

namespace KidTime.SessionAgent;

/// <summary>
/// The language this session's UI is painted in. The service owns the choice - it arrives on
/// every reply from the pipe - so the agent only mirrors it and tells the window when it changed.
/// Until the first reply lands the agent shows English, which is also what the XAML declares.
/// </summary>
internal static class AgentUi
{
    private static AgentStrings _text = AgentStrings.English;

    public static AgentStrings Text => Volatile.Read(ref _text);

    /// <summary>Returns true when the language actually changed and the UI must be repainted.</summary>
    public static bool TrySetLanguage(AgentLanguage language)
    {
        var updated = AgentStrings.For(language);
        if (ReferenceEquals(Volatile.Read(ref _text), updated)) return false;
        Volatile.Write(ref _text, updated);
        ApplyCulture(updated);
        return true;
    }

    /// <summary>
    /// WPF resolves <c>StringFormat</c> and default number formatting through the thread's
    /// culture, and controls capture it when they are created; setting the framework default too
    /// keeps anything created later on the same language.
    /// </summary>
    private static void ApplyCulture(AgentStrings text)
    {
        try
        {
            CultureInfo.CurrentCulture = text.Culture;
            CultureInfo.CurrentUICulture = text.Culture;
            CultureInfo.DefaultThreadCurrentCulture = text.Culture;
            CultureInfo.DefaultThreadCurrentUICulture = text.Culture;
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(
                    System.Windows.Markup.XmlLanguage.GetLanguage(text.Culture.IetfLanguageTag)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // OverrideMetadata throws once the property is already overridden, and the culture is
            // already applied by then. The UI language is what matters, not the metadata default.
            SessionLogger.Information("Interface culture metadata was already set.", exception);
        }
    }
}
