using System.ComponentModel;
using System.Windows;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace KidTime.SessionAgent;

public partial class StatusWindow : FluentWindow
{
    private bool _allowClose;

    public StatusWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        Closing += WindowClosing;
    }

    public void UpdateStatus(SessionStatusSnapshot status)
    {
        var screenTime = status.ScreenTime;
        DailyRing.IsIndeterminate = false;
        if (screenTime.DailyLimitSeconds is int dailyLimit)
        {
            var remaining = screenTime.DailyRemainingSeconds ?? 0;
            DailyRing.Progress = dailyLimit <= 0 ? 0 : Math.Clamp(remaining * 100d / dailyLimit, 0, 100);
            RemainingText.Text = FormatDuration(remaining);
            RemainingCaption.Text = "left today";
            DailyUsageText.Text = $"{FormatDuration(screenTime.TodayActiveSeconds)} used of {FormatDuration(dailyLimit)}";
        }
        else
        {
            DailyRing.Progress = 100;
            RemainingText.Text = "Unlimited";
            RemainingCaption.Text = "no daily limit";
            DailyUsageText.Text = $"{FormatDuration(screenTime.TodayActiveSeconds)} used today";
        }

        OverallBadge.Content = screenTime.IsAllowed ? "Available now" : "Unavailable";
        OverallBadge.Appearance = screenTime.IsAllowed ? ControlAppearance.Success : ControlAppearance.Danger;
        RemainingText.Opacity = screenTime.IsAllowed ? 1 : 0.72;
        RestrictionInfo.IsOpen = !screenTime.IsAllowed;
        RestrictionInfo.Message = screenTime.Message;
        RestrictionInfo.Severity = InfoBarSeverity.Warning;

        ScheduleHeadline.Text = FormatScheduleHeadline(screenTime);
        ScheduleDetail.Text = FormatScheduleDetail(screenTime);
        UpdateScheduleProgress(screenTime, status.GeneratedAtUtc);

        ServerStatusText.Text = status.Server.IsConnected ? "Connected" : "Offline";
        ServerDetailText.Text = status.Server.IsConnected
            ? status.Server.LastSuccessfulContactUtc is { } contact
                ? $"Contact {FormatRelative(contact)}"
                : status.Server.ConnectionMessage
            : status.Server.ConnectionMessage;
        ServerIcon.Symbol = status.Server.IsConnected ? SymbolRegular.CloudCheckmark24 : SymbolRegular.CloudDismiss24;

        var syncFailed = status.Server.LastSynchronizationError is { Length: > 0 };
        SyncStatusText.Text = syncFailed
            ? "Sync needs attention"
            : status.Server.LastSuccessfulSynchronizationUtc is null
                ? "Waiting"
                : FormatRelative(status.Server.LastSuccessfulSynchronizationUtc);
        SyncDetailText.Text = syncFailed
            ? status.Server.LastSynchronizationError!
            : status.Server.LastSuccessfulSynchronizationUtc is null
                ? "No successful synchronization yet"
                : "Rules and usage are synchronized";
        SyncIcon.Symbol = syncFailed ? SymbolRegular.ArrowSyncDismiss24 : SymbolRegular.ArrowSyncCheckmark24;

        ProfileStatusText.Text = status.ControlledUserName ?? "Not selected";
        ProfileDetailText.Text = $"Cached rule revision {status.RuleRevision}";

        var applications = status.Applications
            .OrderBy(item => item.Allowance.IsAllowed)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(BuildApplicationCard)
            .ToList();
        ApplicationsItems.ItemsSource = applications;
        AppsCountBadge.Content = applications.Count.ToString();
        EmptyAppsCard.Visibility = applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationsItems.Visibility = applications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatedText.Text = $"Live status updated {status.GeneratedAtUtc.ToLocalTime():HH:mm:ss}";
    }

    public void CloseForExit()
    {
        _allowClose = true;
        if (IsLoaded) SystemThemeWatcher.UnWatch(this);
        Close();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void UpdateScheduleProgress(TimeAllowanceStatus allowance, DateTimeOffset now)
    {
        if (allowance is
            {
                IsWithinSchedule: true,
                ScheduleAvailableSinceUtc: { } start,
                ScheduleAvailableUntilUtc: { } end
            }
            && end > start)
        {
            ScheduleProgress.Visibility = Visibility.Visible;
            ScheduleProgress.Value = Math.Clamp((end - now).TotalSeconds * 100 / (end - start).TotalSeconds, 0, 100);
            return;
        }

        ScheduleProgress.Visibility = Visibility.Collapsed;
    }

    private static ApplicationCardViewModel BuildApplicationCard(ApplicationTimeStatus application)
    {
        var allowance = application.Allowance;
        var status = application.IsManuallyBlocked
            ? "Blocked"
            : allowance.Reason switch
            {
                BlockReason.DailyLimitReached => "Limit reached",
                BlockReason.OutsideAllowedSchedule => "Outside schedule",
                _ => "Available"
            };
        var appearance = allowance.IsAllowed ? ControlAppearance.Success : ControlAppearance.Danger;
        var dailySummary = allowance.DailyLimitSeconds is int limit
            ? $"{FormatDuration(allowance.DailyRemainingSeconds ?? 0)} left of {FormatDuration(limit)} daily"
            : "No daily limit";
        var dailyPercent = allowance.DailyLimitSeconds is int dailyLimit && dailyLimit > 0
            ? Math.Clamp((allowance.DailyRemainingSeconds ?? 0) * 100d / dailyLimit, 0, 100)
            : 0;
        return new ApplicationCardViewModel(
            application.DisplayName,
            status,
            appearance,
            dailySummary,
            FormatScheduleHeadline(allowance),
            dailyPercent,
            allowance.DailyLimitSeconds is null ? Visibility.Collapsed : Visibility.Visible);
    }

    private static string FormatScheduleHeadline(TimeAllowanceStatus allowance)
    {
        if (!allowance.HasWeeklySchedule) return "Available at any time";
        if (allowance.IsWithinSchedule)
        {
            return allowance.ScheduleAvailableUntilUtc is { } until
                ? $"Available until {FormatDeadline(until)}"
                : "Available now";
        }
        return allowance.ScheduleAvailableAgainUtc is { } available
            ? $"Available again {FormatDeadline(available)}"
            : "Not available this week";
    }

    private static string FormatScheduleDetail(TimeAllowanceStatus allowance)
    {
        if (!allowance.HasWeeklySchedule) return "No weekly schedule restricts screen time.";
        if (allowance.IsWithinSchedule && allowance.ScheduleAvailableUntilUtc is { } until)
            return $"{FormatDurationUntil(until)} remains in the current schedule window.";
        if (!allowance.IsWithinSchedule && allowance.ScheduleAvailableAgainUtc is { } available)
            return $"The next allowed schedule window begins {FormatDeadline(available)}.";
        return allowance.IsWithinSchedule
            ? "The current schedule allows screen time."
            : "No allowed schedule window was found for this week.";
    }

    private static string FormatDuration(int seconds)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(seconds / 60d));
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes} min";
    }

    private static string FormatDurationUntil(DateTimeOffset deadline) =>
        FormatDuration(Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds)));

    private static string FormatDeadline(DateTimeOffset deadline)
    {
        var local = deadline.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date ? $"today at {local:HH:mm}" : local.ToString("ddd at HH:mm");
    }

    private static string FormatRelative(DateTimeOffset? time)
    {
        if (time is null) return "Not yet";
        var elapsed = DateTimeOffset.UtcNow - time.Value;
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} h ago";
        return time.Value.ToLocalTime().ToString("ddd HH:mm");
    }

    private sealed record ApplicationCardViewModel(
        string DisplayName,
        string StatusText,
        ControlAppearance StatusAppearance,
        string DailySummary,
        string ScheduleSummary,
        double DailyRemainingPercent,
        Visibility DailyProgressVisibility);
}
