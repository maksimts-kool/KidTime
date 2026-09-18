using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private readonly Func<TimeExtensionSubmission, CancellationToken, Task<TimeExtensionSubmissionResult>> _requestExtraTime;
    private readonly ObservableCollection<ApplicationCardViewModel> _applications = [];
    private readonly ObservableCollection<FilterCategoryViewModel> _filterCategories = [];
    private readonly ObservableCollection<FilterSiteGroupViewModel> _filterSiteGroups = [];
    private string? _lastRenderedSignature;
    private string _selectedTab = "Today";
    private bool _allowClose;
    private bool _removalDialogOpen;

    // The PC's own offer, which is what the Today card acts on. Application offers ride on their
    // cards instead. The service decides all of them; the window only shows what it was told.
    private TimeExtensionOffer? _extraTimeOffer;
    private string? _lastExtraTimeSignature;
    private bool _extraTimeRequestInFlight;
    private bool _extraTimeDialogOpen;

    private static AgentStrings Text => AgentUi.Text;

    public StatusWindow(
        Func<ParentRemovalRequest, CancellationToken, Task<DeviceRemovalResult>> removeKidTime,
        Func<TimeExtensionSubmission, CancellationToken, Task<TimeExtensionSubmissionResult>> requestExtraTime)
    {
        _removeKidTime = removeKidTime;
        _requestExtraTime = requestExtraTime;
        InitializeComponent();
        ApplicationsItems.ItemsSource = _applications;
        FilterCategoryItems.ItemsSource = _filterCategories;
        FilterSiteGroupItems.ItemsSource = _filterSiteGroups;
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
        InternetTabText.Text = text.TabInternet;
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
        FilterCategoriesTitle.Text = text.WebFilterAlwaysBlocked;
        FilterSiteGroupsTitle.Text = text.WebFilterSiteGroups;
        FilterExplainTitle.Text = text.WebFilterExplainTitle;
        FilterExplainDetail.Text = text.WebFilterExplainDetail;
        PrivacySeesTitle.Text = text.WhatKidTimeSees;
        PrivacySeesDetail.Text = text.WhatKidTimeSeesDetail;
        PrivacyNeverSeesTitle.Text = text.WhatKidTimeNeverSees;
        PrivacyNeverSeesDetail.Text = text.WhatKidTimeNeverSeesDetail;
        RemoveCardTitle.Text = text.RemoveCardTitle;
        RemoveCardDetail.Text = text.RemoveCardDetail;
        RemoveKidTimeButton.Content = text.RemoveButton;
        ExtraTimeTitleText.Text = text.ExtraTimeCardTitle;
        ExtraTimeAskButton.Content = text.ExtraTimeAskButton;
        // The card's live wording comes from the next offer, so force it to be redrawn.
        _lastExtraTimeSignature = null;

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
        _selectedTab = tab;
        TodayPanel.Visibility = tab == "Today" ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = tab == "Apps" ? Visibility.Visible : Visibility.Collapsed;
        InternetPanel.Visibility = tab == "Internet" ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = tab == "Status" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tab == "About" ? Visibility.Visible : Visibility.Collapsed;
        TodayTabButton.Appearance = Selected(tab, "Today");
        AppsTabButton.Appearance = Selected(tab, "Apps");
        InternetTabButton.Appearance = Selected(tab, "Internet");
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
    /// Draws the extra-time card from what the service offered on the last reply. The service
    /// decides whether asking is possible at all - the window never works that out for itself,
    /// because the buttons would then disagree with what the service would actually accept.
    ///
    /// At most one offer is shown: the application the child is looking at when it is running
    /// out, the PC otherwise. Two cards competing for one decision is not a choice a child in the
    /// last five minutes of their time should be made to think about.
    /// </summary>
    public void UpdateExtraTime(IReadOnlyList<TimeExtensionOffer>? offers)
    {
        // The PC only. An application that is running out gets its button on its own card in the
        // Apps tab, where a child looks for it - rather than this card changing identity to
        // whatever happens to be in the foreground.
        var offer = offers?.FirstOrDefault(item => item.ApplicationIdentityKey is null);
        _extraTimeOffer = offer;
        if (offer is null)
        {
            if (_lastExtraTimeSignature is null) return;
            _lastExtraTimeSignature = null;
            ExtraTimeCard.Visibility = Visibility.Collapsed;
            return;
        }

        var text = Text;
        var signature = $"{text.Language}|{offer.ApplicationIdentityKey}|{offer.DisplayName}|{offer.State}|{offer.Minutes}";
        if (signature == _lastExtraTimeSignature) return;
        _lastExtraTimeSignature = signature;

        ExtraTimeCard.Visibility = Visibility.Visible;
        ExtraTimeScopeText.Text = text.ExtraTimeForScope(offer.DisplayName);
        // Only a request still waiting on an answer takes the buttons away. A grant that has been
        // used up, or a refusal, leaves them: the child may ask once more, up to the daily cap the
        // service enforces.
        var waiting = offer.State == TimeExtensionOfferState.Pending;
        ExtraTimeControls.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        ExtraTimeDetailText.Text = offer.State switch
        {
            TimeExtensionOfferState.Pending => text.ExtraTimeWaitingForParent,
            TimeExtensionOfferState.Granted => text.ExtraTimeGrantedCaption(offer.Minutes),
            TimeExtensionOfferState.Denied => text.ExtraTimeDeniedCaption,
            _ => text.ExtraTimeChooseHowMuch
        };
        if (!waiting) return;
        // The answer to the previous press is now the caption above, so the transient bar goes.
        ExtraTimeInfo.IsOpen = false;
    }

    /// <summary>
    /// Brings the window forward and opens the request straight away, for the thing that is
    /// running out. Called from the countdown card and the toast, where the child has already
    /// said what they want - making them find the button again would be a step for nothing.
    /// </summary>
    public void ShowExtraTimeRequest(TimeExtensionOffer? offer)
    {
        SelectTab(offer?.ApplicationIdentityKey is null ? "Today" : "Apps");
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (offer is { State: not TimeExtensionOfferState.Pending })
            _ = RequestExtraTimeAsync(offer, offer.ApplicationIdentityKey is null ? ExtraTimeInfo : AppsExtraTimeInfo);
    }

    private void ExtraTimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_extraTimeOffer is not { } offer) return;
        _ = RequestExtraTimeAsync(offer, ExtraTimeInfo);
    }

    private void ApplicationExtraTimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Control { Tag: string identityKey }) return;
        if (_applications.FirstOrDefault(card => card.IdentityKey == identityKey)?.Extension is not { } offer) return;
        _ = RequestExtraTimeAsync(offer, AppsExtraTimeInfo);
    }

    /// <summary>
    /// The one place an amount is chosen and a request is sent, whichever button started it.
    ///
    /// The slider is built here rather than sitting in a panel because there is one of it and
    /// several things it can be about: the PC, or any application on the Apps tab. Its range comes
    /// from the policy the service checks against, so it cannot offer a stop that would be refused.
    /// </summary>
    private async Task RequestExtraTimeAsync(TimeExtensionOffer offer, InfoBar target)
    {
        // ContentDialog allows one at a time; a second would throw out of an async void handler.
        if (_extraTimeDialogOpen || _extraTimeRequestInFlight) return;
        _extraTimeDialogOpen = true;
        try
        {
            var text = Text;
            target.IsOpen = false;
            var amountText = new System.Windows.Controls.TextBlock
            {
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = text.ExtraTimeAmount(TimeExtensionPolicy.DefaultRequestMinutes)
            };
            var slider = new System.Windows.Controls.Slider
            {
                Minimum = TimeExtensionPolicy.MinimumRequestMinutes,
                Maximum = TimeExtensionPolicy.MaximumRequestMinutes,
                TickFrequency = TimeExtensionPolicy.RequestStepMinutes,
                SmallChange = TimeExtensionPolicy.RequestStepMinutes,
                LargeChange = TimeExtensionPolicy.RequestStepMinutes,
                IsSnapToTickEnabled = true,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                Value = TimeExtensionPolicy.DefaultRequestMinutes,
                Margin = new Thickness(0, 8, 0, 0),
                MinWidth = 360
            };
            System.Windows.Automation.AutomationProperties.SetName(slider, text.ExtraTimeChooseHowMuch);
            slider.ValueChanged += (_, _) =>
                amountText.Text = text.ExtraTimeAmount(SnapMinutes(slider.Value));

            var content = new System.Windows.Controls.StackPanel();
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = text.ExtraTimeForScope(offer.DisplayName),
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = text.ExtraTimeChooseHowMuch,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            content.Children.Add(amountText);
            content.Children.Add(slider);

            var dialog = new ContentDialog(RootContentDialogHost)
            {
                Title = text.ExtraTimeCardTitle,
                Content = content,
                PrimaryButtonText = text.ExtraTimeAskButton,
                CloseButtonText = text.Cancel,
                PrimaryButtonAppearance = ControlAppearance.Primary,
                DefaultButton = ContentDialogButton.Primary,
                DialogWidth = 460
            };
            if (await dialog.ShowAsync(CancellationToken.None) != ContentDialogResult.Primary) return;
            await SubmitExtraTimeAsync(offer, SnapMinutes(slider.Value), target);
        }
        catch (Exception exception)
        {
            SessionLogger.ReportFault(
                DiagnosticSeverities.Error,
                "The KidTime extra-time dialog failed.",
                exception);
        }
        finally
        {
            _extraTimeDialogOpen = false;
        }
    }

    /// <summary>
    /// The slider snaps to its ticks already, but it reports a double and the service accepts
    /// only the stops.
    /// </summary>
    private static int SnapMinutes(double value) =>
        TimeExtensionPolicy.ClampToStep((int)Math.Round(value));

    private async Task SubmitExtraTimeAsync(TimeExtensionOffer offer, int minutes, InfoBar target)
    {
        _extraTimeRequestInFlight = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await _requestExtraTime(
                new TimeExtensionSubmission(minutes, offer.ApplicationIdentityKey),
                timeout.Token);
            ShowExtraTimeResult(target, result.Accepted, result.Message);
            // The service now holds the pending request, so the next reply repaints the cards.
            _lastExtraTimeSignature = null;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            SessionLogger.Information("The extra-time request could not reach the service.", exception);
            ShowExtraTimeResult(target, false, Text.ExtraTimeNotPossible);
        }
        catch (Exception exception)
        {
            SessionLogger.ReportFault(
                DiagnosticSeverities.Error,
                "The KidTime extra-time request failed.",
                exception);
        }
        finally
        {
            _extraTimeRequestInFlight = false;
        }
    }

    private static void ShowExtraTimeResult(InfoBar target, bool accepted, string message)
    {
        target.IsOpen = true;
        target.Severity = accepted ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        target.Title = message;
        target.Message = string.Empty;
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
            DailyUsageText.Text = screenTime.BonusSeconds > 0
                ? $"{text.UsedOf(text.DurationLabel(screenTime.TodayActiveSeconds), text.DurationLabel(dailyLimit))}  ·  " +
                  text.ExtraTimeAddedToday(screenTime.BonusSeconds / 60)
                : text.UsedOf(
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

        RenderWebFiltering(text, status.DnsFiltering);
    }

    /// <summary>
    /// Draws the Internet tab, and decides whether there is a tab at all. A household with no DNS
    /// filter gets no tab: an empty one would suggest something is being done that is not.
    ///
    /// Everything here is a description. The tab has no controls, because the thing it describes
    /// is not KidTime's to change - it belongs to the household's DNS server and to the parent.
    /// </summary>
    private void RenderWebFiltering(AgentStrings text, DnsFilteringSnapshot? filtering)
    {
        var available = filtering is not null && filtering.State != DnsFilteringState.NotConfigured;
        InternetTabButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            // The parent can remove the DNS server while the window is open on this tab.
            if (_selectedTab == "Internet") SelectTab("Today");
            return;
        }

        var snapshot = filtering!;
        var isOn = snapshot.State == DnsFilteringState.Active;
        FilterStateIcon.Symbol = isOn ? SymbolRegular.GlobeShield24 : SymbolRegular.GlobeOff24;
        FilterStateTitle.Text = isOn ? text.WebFilterOnTitle : text.WebFilterOffTitle;
        FilterStateDetail.Text = isOn ? text.WebFilterOnDetail : text.WebFilterOffDetail;
        FilterStateBadge.Content = isOn ? text.BadgeFilterOn : text.BadgeFilterOff;
        FilterStateBadge.Appearance = isOn ? ControlAppearance.Success : ControlAppearance.Secondary;

        // Only said when what is on screen is not current: this PC is offline, or the server itself
        // could not reach the DNS server. Saying it always would be noise on a working PC.
        FilterCheckedText.Visibility = snapshot.IsStale ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot.IsStale)
            FilterCheckedText.Text = text.WebFilterLastChecked(text.Relative(snapshot.RetrievedAtUtc));

        _filterCategories.Clear();
        foreach (var category in snapshot.Categories)
        {
            _filterCategories.Add(new FilterCategoryViewModel(
                text.FilterCategoryName(category.Kind),
                text.WebFilterListCount(category.ListCount),
                CategoryIcon(category.Kind)));
        }

        FilterCategoriesCard.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        FilterCategoriesEmpty.Visibility = _filterCategories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterCategoriesEmpty.Text = text.WebFilterNothingBlocked;
        FilterCategoryItems.Visibility = _filterCategories.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        _filterSiteGroups.Clear();
        foreach (var group in snapshot.SiteGroups)
            _filterSiteGroups.Add(BuildSiteGroupCard(text, group));
        FilterSiteGroupsCard.Visibility = isOn && _filterSiteGroups.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        // "Your parent set up a filter for the whole home network" is only true while there is
        // one, so the explanation goes with it rather than standing over an empty tab.
        FilterExplainCard.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FilterSiteGroupViewModel BuildSiteGroupCard(AgentStrings text, DnsSiteGroup group)
    {
        // The DNS server's own name for a set of sites is whatever the parent typed, usually
        // lower case ("roblox"). It is a proper name on the child's screen, so it is capitalized
        // here rather than in the contract - the server has no business deciding how it reads.
        var name = group.Name.Length > 0
            ? char.ToUpper(group.Name[0], text.Culture) + group.Name[1..]
            : group.Name;
        var detail = group.IsBlockedNow
            ? group.ChangesAtUtc is { } backAt
                ? text.WebFilterBackAt(text.Deadline(backAt))
                : text.WebFilterBlockedAlways
            : group.ChangesAtUtc is { } closesAt
                ? text.WebFilterClosesAt(text.Deadline(closesAt))
                : text.WebFilterNoScheduleYet;
        var hours = string.Join("   ·   ", group.Windows.Select(window => text.WebFilterWindowOnDays(
            text.WebFilterWindow(window.StartTime, window.EndTime),
            FormatDays(text, window.Days))));
        if (group.SiteCount > 0)
        {
            var sites = text.WebFilterSiteCount(group.SiteCount);
            hours = hours.Length == 0 ? sites : $"{hours}   ·   {sites}";
        }

        return new FilterSiteGroupViewModel(
            name,
            detail,
            hours,
            group.IsBlockedNow ? text.BadgeSitesBlocked : text.BadgeSitesAvailable,
            group.IsBlockedNow ? ControlAppearance.Danger : ControlAppearance.Success,
            group.IsBlockedNow ? SymbolRegular.GlobeProhibited24 : SymbolRegular.GlobeClock24);
    }

    private static string FormatDays(AgentStrings text, IReadOnlyList<DayOfWeek> days) =>
        days.Count is 0 or 7
            ? text.WebFilterEveryDay
            : string.Join(", ", days
                .OrderBy(day => ((int)day + 6) % 7)
                .Select(day => text.Culture.DateTimeFormat.GetAbbreviatedDayName(day)));

    private static SymbolRegular CategoryIcon(DnsFilterCategoryKind kind) => kind switch
    {
        DnsFilterCategoryKind.Ads => SymbolRegular.Megaphone24,
        DnsFilterCategoryKind.Trackers => SymbolRegular.EyeOff24,
        DnsFilterCategoryKind.Adult => SymbolRegular.PersonProhibited24,
        DnsFilterCategoryKind.Gambling => SymbolRegular.MoneyDismiss24,
        DnsFilterCategoryKind.Malware => SymbolRegular.ShieldError24,
        DnsFilterCategoryKind.Social => SymbolRegular.PeopleCommunity24,
        _ => SymbolRegular.ShieldCheckmark24
    };

    private sealed record FilterCategoryViewModel(string Name, string Detail, SymbolRegular Icon);

    private sealed record FilterSiteGroupViewModel(
        string Name,
        string Detail,
        string Hours,
        string StatusText,
        ControlAppearance StatusAppearance,
        SymbolRegular Icon);

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
            .Append(screenTime.BonusSeconds).Append('|')
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
            .Append(text.Relative(status.Server.LastSuccessfulContactUtc)).Append('|')
            .Append(DnsSignature(text, status.DnsFiltering)).Append('|');
        foreach (var card in cards)
        {
            builder.Append(card.IdentityKey).Append(':')
                .Append(card.StatusText).Append(':')
                .Append(card.DailySummary).Append(':')
                .Append(card.ScheduleSummary).Append(':')
                .Append(card.ExtraTimeVisibility).Append(';');
        }
        return builder.ToString();
    }

    /// <summary>
    /// Enough of the DNS picture to tell one rendering from another. A site group whose deadline
    /// only moves by seconds must not repaint the window, so the deadline enters the signature as
    /// the wording the child would read rather than as an instant.
    /// </summary>
    private static string DnsSignature(AgentStrings text, DnsFilteringSnapshot? filtering)
    {
        if (filtering is null) return "none";
        var builder = new StringBuilder();
        builder.Append(filtering.State).Append(':')
            .Append(filtering.IsStale).Append(':')
            .Append(text.Relative(filtering.RetrievedAtUtc)).Append(':');
        foreach (var category in filtering.Categories)
            builder.Append(category.Kind).Append('=').Append(category.ListCount).Append(',');
        foreach (var group in filtering.SiteGroups)
        {
            builder.Append(group.Name).Append('=')
                .Append(group.IsBlockedNow).Append('=')
                .Append(group.SiteCount).Append('=')
                .Append(group.ChangesAtUtc is { } change ? text.Deadline(change) : string.Empty)
                .Append(',');
        }

        return builder.ToString();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        // When Windows ends the session the window handle is already gone and UnWatch throws.
        if (IsLoaded && new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero)
            SystemThemeWatcher.UnWatch(this);
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
        // A request already waiting on an answer takes the button away; anything else leaves it.
        var canAsk = application.Extension is { State: not TimeExtensionOfferState.Pending };
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
            DailyProgressVisibility = allowance.DailyLimitSeconds is null ? Visibility.Collapsed : Visibility.Visible,
            Extension = application.Extension,
            ExtraTimeButtonText = text.ExtraTimeAskButton,
            ExtraTimeVisibility = canAsk ? Visibility.Visible : Visibility.Collapsed
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
        private string _extraTimeButtonText = string.Empty;
        private Visibility _extraTimeVisibility = Visibility.Collapsed;
        private TimeExtensionOffer? _extension;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string IdentityKey { get; } = identityKey;

        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
        public ControlAppearance StatusAppearance { get => _statusAppearance; set => Set(ref _statusAppearance, value); }
        public string DailySummary { get => _dailySummary; set => Set(ref _dailySummary, value); }
        public string ScheduleSummary { get => _scheduleSummary; set => Set(ref _scheduleSummary, value); }
        public double DailyRemainingPercent { get => _dailyRemainingPercent; set => Set(ref _dailyRemainingPercent, value); }
        public Visibility DailyProgressVisibility { get => _dailyProgressVisibility; set => Set(ref _dailyProgressVisibility, value); }
        public string ExtraTimeButtonText { get => _extraTimeButtonText; set => Set(ref _extraTimeButtonText, value); }
        public Visibility ExtraTimeVisibility { get => _extraTimeVisibility; set => Set(ref _extraTimeVisibility, value); }

        /// <summary>What the button acts on. Not bound - the click handler reads it.</summary>
        public TimeExtensionOffer? Extension { get => _extension; set => Set(ref _extension, value); }

        public void CopyFrom(ApplicationCardViewModel other)
        {
            DisplayName = other.DisplayName;
            StatusText = other.StatusText;
            StatusAppearance = other.StatusAppearance;
            DailySummary = other.DailySummary;
            ScheduleSummary = other.ScheduleSummary;
            DailyRemainingPercent = other.DailyRemainingPercent;
            DailyProgressVisibility = other.DailyProgressVisibility;
            ExtraTimeButtonText = other.ExtraTimeButtonText;
            ExtraTimeVisibility = other.ExtraTimeVisibility;
            Extension = other.Extension;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
