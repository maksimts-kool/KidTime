using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace KidTime.SessionAgent;

public partial class StatusWindow : FluentWindow
{
    private readonly Func<ParentRemovalRequest, CancellationToken, Task<DeviceRemovalResult>> _removeKidTime;
    private readonly ObservableCollection<ApplicationCardViewModel> _applications = [];
    private string? _lastRenderedSignature;
    private bool _allowClose;
    private bool _removalDialogOpen;

    public StatusWindow(Func<ParentRemovalRequest, CancellationToken, Task<DeviceRemovalResult>> removeKidTime)
    {
        _removeKidTime = removeKidTime;
        InitializeComponent();
        ApplicationsItems.ItemsSource = _applications;
        VersionText.Text = $"Version {SessionLogger.Version}";
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        Closing += WindowClosing;
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Control { Tag: string tab }) SelectTab(tab);
    }

    private void SelectTab(string tab)
    {
        TodayPanel.Visibility = tab == "Today" ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = tab == "Apps" ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = tab == "Status" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tab == "About" ? Visibility.Visible : Visibility.Collapsed;
        TodayTabButton.Appearance = Selected(tab, "Today");
        AppsTabButton.Appearance = Selected(tab, "Apps");
        StatusTabButton.Appearance = Selected(tab, "Status");
        AboutTabButton.Appearance = Selected(tab, "About");
    }

    private static ControlAppearance Selected(string tab, string candidate) =>
        tab == candidate ? ControlAppearance.Primary : ControlAppearance.Secondary;

    private async void RemoveKidTimeButton_Click(object sender, RoutedEventArgs e)
    {
        // ContentDialog allows one dialog at a time; a second click while the first is open
        // would throw straight out of an async void handler and take the agent down.
        if (_removalDialogOpen) return;
        _removalDialogOpen = true;
        try
        {
            await RequestRemovalAsync();
        }
        catch (Exception exception)
        {
            SessionLogger.ReportFault(
                DiagnosticSeverities.Error,
                "The KidTime removal dialog failed.",
                exception);
            ShowRemovalResult(new DeviceRemovalResult(false, "Something went wrong. Try again in a moment."));
            RemoveKidTimeButton.IsEnabled = true;
        }
        finally
        {
            _removalDialogOpen = false;
        }
    }

    private async Task RequestRemovalAsync()
    {
        RemovalInfo.IsOpen = false;
        var emailBox = new System.Windows.Controls.TextBox
        {
            MaxLength = 320,
            MinWidth = 380,
            Margin = new Thickness(0, 5, 0, 14)
        };
        System.Windows.Automation.AutomationProperties.SetName(emailBox, "Parent email address");
        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            MaxLength = 1024,
            MinWidth = 380,
            Margin = new Thickness(0, 5, 0, 8)
        };
        System.Windows.Automation.AutomationProperties.SetName(passwordBox, "Parent password");
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "This permanently removes KidTime services and local data. The server must be reachable to verify the parent account.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Parent email address",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(emailBox);
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Parent password",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(passwordBox);

        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = "Remove KidTime from this PC?",
            Content = content,
            PrimaryButtonText = "Verify and remove",
            CloseButtonText = "Cancel",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            DefaultButton = ContentDialogButton.Primary,
            DialogWidth = 500
        };
        var choice = await dialog.ShowAsync(CancellationToken.None);
        if (choice != ContentDialogResult.Primary) return;

        var email = emailBox.Text;
        var password = passwordBox.Password;
        passwordBox.Clear();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            ShowRemovalResult(new DeviceRemovalResult(false, "Enter the parent email address and password."));
            return;
        }

        RemoveKidTimeButton.IsEnabled = false;
        RemovalInfo.IsOpen = true;
        RemovalInfo.Severity = InfoBarSeverity.Informational;
        RemovalInfo.Title = "Checking parent account";
        RemovalInfo.Message = "KidTime is securely verifying the login with the server.";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await _removeKidTime(new ParentRemovalRequest(email, password), timeout.Token);
        ShowRemovalResult(result);
        RemoveKidTimeButton.IsEnabled = !result.Accepted;
    }

    private void ShowRemovalResult(DeviceRemovalResult result)
    {
        RemovalInfo.IsOpen = true;
        RemovalInfo.Severity = result.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        RemovalInfo.Title = result.Accepted ? "Removal started" : "KidTime was not removed";
        RemovalInfo.Message = result.Message;
    }

    /// <summary>
    /// Called for every status the service sends. Rendering is skipped when nothing a person
    /// can see has changed, and the application list is updated in place, so an open window
    /// does not re-run layout for the whole page every two seconds.
    /// </summary>
    public void UpdateStatus(SessionStatusSnapshot status)
    {
        var cards = status.Applications
            .OrderBy(item => item.Allowance.IsAllowed)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(BuildApplicationCard)
            .ToList();

        // The timestamp is one text change and is the only thing that proves the window is live.
        UpdatedText.Text = $"Live status updated {status.GeneratedAtUtc.ToLocalTime():HH:mm:ss}";

        var signature = BuildSignature(status, cards);
        if (signature == _lastRenderedSignature) return;
        _lastRenderedSignature = signature;

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
        HeadlineSubtitle.Text = status.ControlledUserName is { Length: > 0 } profile
            ? $"Signed in as {profile}"
            : "A live view of screen time, schedules, and controlled apps.";
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

        MergeApplications(cards);
        AppsCountBadge.Content = cards.Count.ToString();
        EmptyAppsCard.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationsItems.Visibility = cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Replacing the item source rebuilds every card template. Matching on identity and writing
    /// only the changed values keeps the list still while the numbers move.
    /// </summary>
    private void MergeApplications(IReadOnlyList<ApplicationCardViewModel> cards)
    {
        for (var index = _applications.Count - 1; index >= 0; index--)
        {
            if (!cards.Any(card => card.IdentityKey == _applications[index].IdentityKey))
                _applications.RemoveAt(index);
        }

        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            var existing = _applications.FirstOrDefault(item => item.IdentityKey == card.IdentityKey);
            if (existing is null)
            {
                _applications.Insert(Math.Min(index, _applications.Count), card);
                continue;
            }

            existing.CopyFrom(card);
            var currentIndex = _applications.IndexOf(existing);
            if (currentIndex != index) _applications.Move(currentIndex, index);
        }
    }

    private static string BuildSignature(SessionStatusSnapshot status, IReadOnlyList<ApplicationCardViewModel> cards)
    {
        var builder = new StringBuilder();
        var screenTime = status.ScreenTime;
        builder.Append(screenTime.IsAllowed).Append('|')
            .Append(screenTime.Message).Append('|')
            .Append(FormatDuration(screenTime.TodayActiveSeconds)).Append('|')
            .Append(screenTime.DailyLimitSeconds).Append('|')
            .Append(FormatDuration(screenTime.DailyRemainingSeconds ?? 0)).Append('|')
            .Append(FormatScheduleHeadline(screenTime)).Append('|')
            .Append(FormatScheduleDetail(screenTime)).Append('|')
            .Append((int)ScheduleProgressValue(screenTime, status.GeneratedAtUtc)).Append('|')
            .Append(status.ControlledUserName).Append('|')
            .Append(status.RuleRevision).Append('|')
            .Append(status.Server.IsConnected).Append('|')
            .Append(status.Server.ConnectionMessage).Append('|')
            .Append(status.Server.LastSynchronizationError).Append('|')
            .Append(FormatRelative(status.Server.LastSuccessfulSynchronizationUtc)).Append('|')
            .Append(FormatRelative(status.Server.LastSuccessfulContactUtc)).Append('|');
        foreach (var card in cards)
        {
            builder.Append(card.IdentityKey).Append(':')
                .Append(card.StatusText).Append(':')
                .Append(card.DailySummary).Append(':')
                .Append(card.ScheduleSummary).Append(';');
        }
        return builder.ToString();
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
        var value = ScheduleProgressValue(allowance, now);
        ScheduleProgress.Visibility = value < 0 ? Visibility.Collapsed : Visibility.Visible;
        if (value >= 0) ScheduleProgress.Value = value;
    }

    /// <summary>Percentage of the current schedule window still left, or -1 when there is none.</summary>
    private static double ScheduleProgressValue(TimeAllowanceStatus allowance, DateTimeOffset now) =>
        allowance is
        {
            IsWithinSchedule: true,
            ScheduleAvailableSinceUtc: { } start,
            ScheduleAvailableUntilUtc: { } end
        } && end > start
            ? Math.Clamp((end - now).TotalSeconds * 100 / (end - start).TotalSeconds, 0, 100)
            : -1;

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
        return new ApplicationCardViewModel(application.IdentityKey)
        {
            DisplayName = application.DisplayName,
            StatusText = status,
            StatusAppearance = appearance,
            DailySummary = dailySummary,
            ScheduleSummary = FormatScheduleHeadline(allowance),
            DailyRemainingPercent = dailyPercent,
            DailyProgressVisibility = allowance.DailyLimitSeconds is null ? Visibility.Collapsed : Visibility.Visible
        };
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

    private sealed class ApplicationCardViewModel(string identityKey) : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;
        private string _statusText = string.Empty;
        private ControlAppearance _statusAppearance;
        private string _dailySummary = string.Empty;
        private string _scheduleSummary = string.Empty;
        private double _dailyRemainingPercent;
        private Visibility _dailyProgressVisibility;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string IdentityKey { get; } = identityKey;

        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
        public ControlAppearance StatusAppearance { get => _statusAppearance; set => Set(ref _statusAppearance, value); }
        public string DailySummary { get => _dailySummary; set => Set(ref _dailySummary, value); }
        public string ScheduleSummary { get => _scheduleSummary; set => Set(ref _scheduleSummary, value); }
        public double DailyRemainingPercent { get => _dailyRemainingPercent; set => Set(ref _dailyRemainingPercent, value); }
        public Visibility DailyProgressVisibility { get => _dailyProgressVisibility; set => Set(ref _dailyProgressVisibility, value); }

        public void CopyFrom(ApplicationCardViewModel other)
        {
            DisplayName = other.DisplayName;
            StatusText = other.StatusText;
            StatusAppearance = other.StatusAppearance;
            DailySummary = other.DailySummary;
            ScheduleSummary = other.ScheduleSummary;
            DailyRemainingPercent = other.DailyRemainingPercent;
            DailyProgressVisibility = other.DailyProgressVisibility;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
