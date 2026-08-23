using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace KidTime.SessionAgent;

internal static class NativeWindowsNotification
{
    public static void Show(string title, string message)
    {
        try
        {
            var content = new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .AddAudio(new Uri("ms-winsoundevent:Notification.Reminder"))
                .GetToastContent();
            var xml = new XmlDocument();
            xml.LoadXml(content.GetContent());
            xml.DocumentElement.SetAttribute("scenario", "urgent");
            var toast = new ToastNotification(xml)
            {
                Tag = Guid.NewGuid().ToString("N")[..16],
                Group = "KidTime",
                ExpirationTime = DateTimeOffset.Now.AddMinutes(15),
                Priority = ToastNotificationPriority.High
            };
            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
            SessionLogger.Information($"Urgent native Windows notification shown: {title}");
        }
        catch (Exception exception)
        {
            SessionLogger.Information($"Native Windows notification failed: {title}", exception);
        }
    }
}
