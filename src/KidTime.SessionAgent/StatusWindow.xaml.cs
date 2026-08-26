using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;
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

    private static AgentStrings Text => AgentUi.Text;

    public StatusWindow(Func<ParentRemovalRequest, CancellationToken, Task<DeviceRemovalResult>> removeKidTime)
    {
        _removeKidTime = removeKidTime;
        InitializeComponent();
        ApplicationsItems.ItemsSource = _applications;
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        ApplyLanguage();
        Closing += WindowClosing;
    }

    /// <summary>
    /// Writes every fixed label in the current language. The XAML still declares the English
    /// wording so the designer shows something real; this runs at construction and again whenever
    /// the parent switches the language, and the next status render repaints the live values.
    /// </summary>
    public void ApplyLanguage()
    {
        var text = Text;
        HeadlineTitle.Text = text.HeadlineToday;
        TodayTabText.Text = text.TabToday;
        AppsTabText.Text = text.TabApps;
        StatusTabText.Text = text.TabConnection;
        AboutTabText.Text = text.TabAbout;
        DailyScreenTimeTitle.Text = text.DailyScreenTime;
        WeeklyScheduleCaption.Text = text.WeeklyScheduleCaption;
        RestrictionInfo.Title = text.ScreenTimeUnavailableTitle;
        EmptyAppsTitle.Text = text.NoAppLimitsTitle;
        EmptyAppsDetail.Text = text.NoAppLimitsDetail;
        ServerCaptionText.Text = text.ServerCaption;
        SyncCaptionText.Text = text.SyncCaption;
        ProfileCaptionText.Text = text.ProfileCaption;
        VersionText.Text = text.VersionWithNumber(SessionLogger.Version);
        UpdateChannelText.Text = text.UpdatesInBackground;
        PrivacySeesTitle.Text = text.WhatKidTimeSees;
        PrivacySeesDetail.Text = text.WhatKidTimeSeesDetail;
        PrivacyNeverSeesTitle.Text = text.WhatKidTimeNeverSees;
        PrivacyNeverSeesDetail.Text = text.WhatKidTimeNeverSeesDetail;
        RemoveCardTitle.Text = text.RemoveCardTitle;
        RemoveCardDetail.Text = text.RemoveCardDetail;
        RemoveKidTimeButton.Content = text.RemoveButton;

        if (_lastRenderedSignature is not null) return;
        // Nothing has been rendered yet, so the placeholders are still on screen.
        HeadlineSubtitle.Text = text.HeadlineConnecting;
        OverallBadge.Content = text.BadgeConnecting;
        RemainingCaption.Text = text.LoadingTime;
        DailyUsageText.Text = text.WaitingForService;
        ScheduleHeadline.Text = text.LoadingSchedule;
        ScheduleDetail.Text = text.SchedulePlaceholder;
        ServerStatusText.Text = text.BadgeConnecting;
        ServerDetailText.Text = text.NoContactYet;
        SyncStatusText.Text = text.Waiting;
        SyncDetailText.Text = text.CachedRulesActive;
        ProfileStatusText.Text = text.TrayLoading;
        ProfileDetailText.Text = text.RuleRevisionPlaceholder;
        UpdatedText.Text = text.WaitingForLiveStatus;
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
            ShowRemovalResult(new DeviceRemovalResult(false, Text.RemovalDialogFailed));
            RemoveKidTimeButton.IsEnabled = true;
        }
        finally
        {
            _removalDialogOpen = false;
        }
    }

    private async Task RequestRemovalAsync()
    {
        var text = Text;
        RemovalInfo.IsOpen = false;
        var emailBox = new System.Windows.Controls.TextBox
        {
            MaxLength = 320,
            MinWidth = 380,
            Margin = new Thickness(0, 5, 0, 14)
        };
        System.Windows.Automation.AutomationProperties.SetName(emailBox, text.ParentEmail);
        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            MaxLength = 1024,
            MinWidth = 380,
            Margin = new Thickness(0, 5, 0, 8)
        };
        System.Windows.Automation.AutomationProperties.SetName(passwordBox, text.ParentPassword);
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = text.RemoveDialogIntro,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = text.ParentEmail,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(emailBox);
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = text.ParentPassword,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(passwordBox);

        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = text.RemoveDialogTitle,
            Content = content,
            PrimaryButtonText = text.VerifyAndRemove,
            CloseButtonText = text.Cancel,
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
            ShowRemovalResult(new DeviceRemovalResult(false, text.RemovalEnterCredentials));
            return;
        }

        RemoveKidTimeButton.IsEnabled = false;
        RemovalInfo.IsOpen = true;
        RemovalInfo.Severity = InfoBarSeverity.Informational;
        RemovalInfo.Title = text.CheckingParentAccount;
        RemovalInfo.Message = text.CheckingParentAccountDetail;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await _removeKidTime(new ParentRemovalRequest(email, password), timeout.Token);
        ShowRemovalResult(result);
        RemoveKidTimeButton.IsEnabled = !result.Accepted;
    }

    private void ShowRemovalResult(DeviceRemovalResult result)
    {
        RemovalInfo.IsOpen = true;
        RemovalInfo.Severity = result.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        RemovalInfo.Title = result.Accepted ? Text.RemovalStarted : Text.RemovalNotDone;
        RemovalInfo.Message = result.Message;
    }

    /// <summary>
    /// Called for every status the service sends. Rendering is skipped when nothing a person
    /// can see has changed, and the application list is updated in place, so an open window
    /// does not re-run layout for the whole page every two seconds.
    /// </summary>
    public void UpdateStatus(SessionStatusSnapshot status)
    {
        var text = Text;
        var cards = status.Applications
            .OrderBy(item => item.Allowance.IsAllowed)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => BuildApplicationCard(text, item))
            .ToList();

        // The timestamp is one text change and is the only thing that proves the window is live.
        UpdatedText.Text = text.LiveStatusUpdated(
            status.GeneratedAtUtc.ToLocalTime().ToString("HH:mm:ss", text.Culture));

        var signature = BuildSignature(text, status, cards);
        if (signature == _lastRenderedSignature) return;
        _lastRenderedSignature = signature;

        var screenTime = status.ScreenTime;
        DailyRing.IsIndeterminate = false;
        if (screenTime.DailyLimitSeconds is int dailyLimit)
        {
            var remaining = screenTime.DailyRemainingSeconds ?? 0;
            DailyRing.Progress = dailyLimit <= 0 ? 0 : Math.Clamp(remaining * 100d / dailyLimit, 0, 100);
            RemainingText.Text = text.DurationLabel(remaining);
            RemainingCaption.Text = text.LeftToday;
            DailyUsageText.Text = text.UsedOf(
                text.DurationLabel(screenTime.TodayActiveSeconds),
                text.DurationLabel(dailyLimit));
        }
        else
        {
            DailyRing.Progress = 100;
            RemainingText.Text = text.Unlimited;
            RemainingCaption.Text = text.NoDailyLimitCaption;
            DailyUsageText.Text = text.UsedToday(text.DurationLabel(screenTime.TodayActiveSeconds));
        }

        OverallBadge.Content = screenTime.IsAllowed ? text.BadgeAvailableNow : text.BadgeUnavailable;
        OverallBadge.Appearance = screenTime.IsAllowed ? ControlAppearance.Success : ControlAppearance.Danger;
        HeadlineSubtitle.Text = status.ControlledUserName is { Length: > 0 } profile
            ? text.HeadlineSignedInAs(profile)
            : text.HeadlineLiveView;
        RemainingText.Opacity = screenTime.IsAllowed ? 1 : 0.72;
        RestrictionInfo.IsOpen = !screenTime.IsAllowed;
        RestrictionInfo.Message = screenTime.Message;
        RestrictionInfo.Severity = InfoBarSeverity.Warning;

        ScheduleHeadline.Text = FormatScheduleHeadline(text, screenTime);
        ScheduleDetail.Text = FormatScheduleDetail(text, screenTime);
        UpdateScheduleProgress(screenTime, status.GeneratedAtUtc);

        ServerStatusText.Text = status.Server.IsConnected ? text.Connected : text.Offline;
        ServerDetailText.Text = status.Server.IsConnected
            ? status.Server.LastSuccessfulContactUtc is { } contact
                ? text.ContactRelative(text.Relative(contact))
                : ConnectionMessage(text, status.Server.State)
            : ConnectionMessage(text, status.Server.State);
        ServerIcon.Symbol = status.Server.IsConnected ? SymbolRegular.CloudCheckmark24 : SymbolRegular.CloudDismiss24;

        var syncFailed = status.Server.LastSynchronizationError is { Length: > 0 };
        SyncStatusText.Text = syncFailed
            ? text.SyncNeedsAttention
            : status.Server.LastSuccessfulSynchronizationUtc is null
                ? text.Waiting
                : text.Relative(status.Server.LastSuccessfulSynchronizationUtc);
        SyncDetailText.Text = syncFailed
            ? status.Server.LastSynchronizationError!
            : status.Server.LastSuccessfulSynchronizationUtc is null
                ? text.NoSuccessfulSyncYet
                : text.RulesSynchronized;
        SyncIcon.Symbol = syncFailed ? SymbolRegular.ArrowSyncDismiss24 : SymbolRegular.ArrowSyncCheckmark24;

        ProfileStatusText.Text = status.ControlledUserName ?? text.NotSelected;
        ProfileDetailText.Text = text.CachedRuleRevision(status.RuleRevision);

        MergeApplications(cards);
        AppsCountBadge.Content = cards.Count.ToString(text.Culture);
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

    private static string BuildSignature(
        AgentStrings text,
        SessionStatusSnapshot status,
        IReadOnlyList<ApplicationCardViewModel> cards)
    {
        var builder = new StringBuilder();
        var screenTime = status.ScreenTime;
        builder.Append(text.Language).Append('|')
            .Append(screenTime.IsAllowed).Append('|')
            .Append(screenTime.Message).Append('|')
            .Append(text.DurationLabel(screenTime.TodayActiveSeconds)).Append('|')
            .Append(screenTime.DailyLimitSeconds).Append('|')
            .Append(text.DurationLabel(screenTime.DailyRemainingSeconds ?? 0)).Append('|')
            .Append(FormatScheduleHeadline(text, screenTime)).Append('|')
            .Append(FormatScheduleDetail(text, screenTime)).Append('|')
            .Append((int)ScheduleProgressValue(screenTime, status.GeneratedAtUtc)).Append('|')
            .Append(status.ControlledUserName).Append('|')
            .Append(status.RuleRevision).Append('|')
            .Append(status.Server.IsConnected).Append('|')
            .Append(status.Server.State).Append('|')
            .Append(status.Server.LastSynchronizationError).Append('|')
            .Append(text.Relative(status.Server.LastSuccessfulSynchronizationUtc)).Append('|')
            .Append(text.Relative(status.Server.LastSuccessfulContactUtc)).Append('|');
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

    private static string ConnectionMessage(AgentStrings text, ServerConnectionState state) => state switch
    {
        ServerConnectionState.NotEnrolled => text.DeviceNotEnrolled,
        ServerConnectionState.Connected => text.Connected,
        ServerConnectionState.Offline => text.OfflineCachedRules,
        _ => text.ConnectingToServer
    };

    private static ApplicationCardViewModel BuildApplicationCard(AgentStrings text, ApplicationTimeStatus application)
    {
        var allowance = application.Allowance;
        var status = application.IsManuallyBlocked
            ? text.AppStatusBlocked
            : allowance.Reason switch
            {
                BlockReason.DailyLimitReached => text.AppStatusLimitReached,
                BlockReason.OutsideAllowedSchedule => text.AppStatusOutsideSchedule,
                _ => text.AppStatusAvailable
            };
        var appearance = allowance.IsAllowed ? ControlAppearance.Success : ControlAppearance.Danger;
        var dailySummary = allowance.DailyLimitSeconds is int limit
            ? text.AppDailySummary(
                text.DurationLabel(allowance.DailyRemainingSeconds ?? 0),
                text.DurationLabel(limit))
            : text.AppNoDailyLimit;
        var dailyPercent = allowance.DailyLimitSeconds is int dailyLimit && dailyLimit > 0
            ? Math.Clamp((allowance.DailyRemainingSeconds ?? 0) * 100d / dailyLimit, 0, 100)
            : 0;
        return new ApplicationCardViewModel(application.IdentityKey)
        {
            DisplayName = application.DisplayName,
            StatusText = status,
            StatusAppearance = appearance,
            DailySummary = dailySummary,
            ScheduleSummary = FormatScheduleHeadline(text, allowance),
            DailyRemainingPercent = dailyPercent,
            DailyProgressVisibility = allowance.DailyLimitSeconds is null ? Visibility.Collapsed : Visibility.Visible
        };
    }

    private static string FormatScheduleHeadline(AgentStrings text, TimeAllowanceStatus allowance)
    {
        if (!allowance.HasWeeklySchedule) return text.ScheduleAlwaysAvailable;
        if (allowance.IsWithinSchedule)
        {
            return allowance.ScheduleAvailableUntilUtc is { } until
                ? text.ScheduleAvailableUntil(text.Deadline(until))
                : text.ScheduleAvailableNow;
        }
        return allowance.ScheduleAvailableAgainUtc is { } available
            ? text.ScheduleAvailableAgain(text.Deadline(available))
            : text.ScheduleNoneThisWeek;
    }

    private static string FormatScheduleDetail(AgentStrings text, TimeAllowanceStatus allowance)
    {
        if (!allowance.HasWeeklySchedule) return text.ScheduleNoRestriction;
        if (allowance.IsWithinSchedule && allowance.ScheduleAvailableUntilUtc is { } until)
            return text.ScheduleRemainsInWindow(text.DurationLabel(
                Math.Max(0, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds))));
        if (!allowance.IsWithinSchedule && allowance.ScheduleAvailableAgainUtc is { } available)
            return text.ScheduleNextWindowBegins(text.Deadline(available));
        return allowance.IsWithinSchedule ? text.ScheduleCurrentAllows : text.ScheduleNoWindowFound;
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
