using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace KidTime.SessionAgent;

internal static class NativeWindowsNotification
{
    private const string UrgentGroup = "KidTimeFinalWarning";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ToastNotification> KeyedUrgentToasts = new(StringComparer.Ordinal);
    private static readonly TimeSpan ReminderLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Shows an unkeyed toast. The urgent scenario stays on screen and overrides Focus Assist, and
    /// is reserved for warnings the child has to act on; rule changes, availability, and completed
    /// updates must not interrupt like that.
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

    /// <summary>
    /// An urgent reminder that takes the place of the previous one for the same limit. Urgent
    /// toasts stay on screen until they are dismissed, so 15, 5, and 2 minutes remaining would
    /// otherwise leave three banners stacked in the corner - which is its own way of not being
    /// read.
    /// </summary>
    public static void ShowUrgentReminder(string reminderKey, string title, string message) =>
        ShowKeyedUrgent(reminderKey, title, message, ReminderLifetime, "reminder");

    public static void ShowFinalWarning(string warningKey, string title, string message, int countdownSeconds) =>
        ShowKeyedUrgent(
            warningKey,
            title,
            message,
            TimeSpan.FromSeconds(Math.Max(10, countdownSeconds + 5)),
            "final warning");

    /// <summary>
    /// Shows one urgent toast per key, retiring whatever stood under that key before it. The tag
    /// is derived from the key rather than random, so the replacement also clears the previous
    /// toast out of Notification Center instead of leaving a stale copy there.
    /// </summary>
    private static void ShowKeyedUrgent(
        string warningKey,
        string title,
        string message,
        TimeSpan lifetime,
        string description)
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            var tag = CreateTag(warningKey);
            ToastNotification? previous;
            lock (Gate)
            {
                KeyedUrgentToasts.Remove(warningKey, out previous);
            }
            if (previous is not null) TryHide(notifier, previous);
            RemoveFromHistory(tag);

            // Two short lines only: the title carries the time left, the body carries the reason.
            // A child reading an urgent toast has seconds, not paragraphs.
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
                Group = UrgentGroup,
                ExpirationTime = DateTimeOffset.Now.Add(lifetime),
                Priority = ToastNotificationPriority.High
            };
            toast.Dismissed += (_, _) => RemoveTrackedWarning(warningKey, toast);
            lock (Gate) KeyedUrgentToasts[warningKey] = toast;
            notifier.Show(toast);
            SessionLogger.Information($"Native Windows {description} shown: {title}");
        }
        catch (Exception exception)
        {
            SessionLogger.Information($"Native Windows {description} failed: {title}", exception);
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
                KeyedUrgentToasts.Remove(warningKey, out toast);
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
            if (KeyedUrgentToasts.TryGetValue(warningKey, out var tracked) && ReferenceEquals(tracked, toast))
                KeyedUrgentToasts.Remove(warningKey);
        }
    }

    private static string CreateTag(string warningKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(warningKey)))[..16];

    private static void RemoveFromHistory(string tag)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(tag, UrgentGroup);
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
