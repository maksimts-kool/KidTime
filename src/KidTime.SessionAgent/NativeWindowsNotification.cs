using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace KidTime.SessionAgent;

internal static class NativeWindowsNotification
{
    private const string FinalWarningGroup = "KidTimeFinalWarning";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ToastNotification> FinalWarnings = new(StringComparer.Ordinal);

    /// <summary>
    /// Shows an ordinary toast. Only the final warning before a forced sign-out or close uses
    /// the urgent scenario, which stays on screen and overrides Focus Assist; reminders, rule
    /// changes, availability, and completed updates must not interrupt like that.
    /// </summary>
    public static void Show(string title, string message, bool isUrgent = false)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);
            if (isUrgent) builder.AddAudio(new Uri("ms-winsoundevent:Notification.Reminder"));
            var xml = new XmlDocument();
            xml.LoadXml(builder.GetToastContent().GetContent());
            if (isUrgent) xml.DocumentElement.SetAttribute("scenario", "urgent");
            var toast = new ToastNotification(xml)
            {
                Tag = Guid.NewGuid().ToString("N")[..16],
                Group = "KidTime",
                ExpirationTime = DateTimeOffset.Now.AddMinutes(isUrgent ? 15 : 5),
                Priority = isUrgent ? ToastNotificationPriority.High : ToastNotificationPriority.Default
            };
            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
            SessionLogger.Information($"Native Windows notification shown ({(isUrgent ? "urgent" : "normal")}): {title}");
        }
        catch (Exception exception)
        {
            SessionLogger.Information($"Native Windows notification failed: {title}", exception);
        }
    }

    public static void ShowFinalWarning(string warningKey, string title, string message, int countdownSeconds)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            var tag = CreateTag(warningKey);
            ToastNotification? previous;
            lock (Gate)
            {
                FinalWarnings.Remove(warningKey, out previous);
            }
            if (previous is not null) TryHide(notifier, previous);
            RemoveFromHistory(tag);

            // Two short lines only: the title carries the countdown, the body carries the
            // reason. A child reading an urgent toast has seconds, not paragraphs.
            var content = new ToastContentBuilder()
                .SetToastDuration(ToastDuration.Long)
                .AddText(title)
                .AddText(message)
                .AddAudio(new Uri("ms-winsoundevent:Notification.Reminder"))
                .GetToastContent();
            var xml = new XmlDocument();
            xml.LoadXml(content.GetContent());
            xml.DocumentElement.SetAttribute("scenario", "urgent");
            var toast = new ToastNotification(xml)
            {
                Tag = tag,
                Group = FinalWarningGroup,
                ExpirationTime = DateTimeOffset.Now.AddSeconds(Math.Max(10, countdownSeconds + 5)),
                Priority = ToastNotificationPriority.High
            };
            toast.Dismissed += (_, _) => RemoveTrackedWarning(warningKey, toast);
            lock (Gate) FinalWarnings[warningKey] = toast;
            notifier.Show(toast);
            SessionLogger.Information($"Native Windows final warning shown: {title}");
        }
        catch (Exception exception)
        {
            SessionLogger.Information($"Native Windows final warning failed: {title}", exception);
        }
    }

    public static void DismissFinalWarning(string warningKey)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            ToastNotification? toast;
            lock (Gate)
            {
                FinalWarnings.Remove(warningKey, out toast);
            }
            if (toast is not null) TryHide(notifier, toast);
            RemoveFromHistory(CreateTag(warningKey));
            SessionLogger.Information($"Native Windows final warning dismissed: {warningKey}");
        }
        catch (Exception exception)
        {
            SessionLogger.Information($"Native Windows final warning dismissal failed: {warningKey}", exception);
        }
    }

    public static void Unregister()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
            SessionLogger.Information("KidTime native notification registration removed.");
        }
        catch (Exception exception)
        {
            SessionLogger.Information("KidTime native notification registration cleanup failed.", exception);
        }
    }

    private static void RemoveTrackedWarning(string warningKey, ToastNotification toast)
    {
        lock (Gate)
        {
            if (FinalWarnings.TryGetValue(warningKey, out var tracked) && ReferenceEquals(tracked, toast))
                FinalWarnings.Remove(warningKey);
        }
    }

    private static string CreateTag(string warningKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(warningKey)))[..16];

    private static void RemoveFromHistory(string tag)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(tag, FinalWarningGroup);
        }
        catch (Exception exception)
        {
            SessionLogger.Information($"Native Windows notification history cleanup failed: {tag}", exception);
        }
    }

    private static void TryHide(ToastNotifierCompat notifier, ToastNotification toast)
    {
        try
        {
            notifier.Hide(toast);
        }
        catch (Exception exception)
        {
            SessionLogger.Information($"Native Windows notification hide failed: {toast.Tag}", exception);
        }
    }
}
