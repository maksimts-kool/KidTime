using System.Globalization;
using KidTime.Domain.Contracts;

namespace KidTime.Domain.Localization;

/// <summary>
/// Every string the controlled user can read, in one place. Both halves of the agent depend on
/// it: ControlService composes notifications and rule messages, SessionAgent paints the tray
/// dashboard and the countdown card. Making it an abstract class rather than a resource
/// dictionary is deliberate - a new message cannot be added in one language only, because the
/// other language then fails to compile.
/// </summary>
public abstract class AgentStrings
{
    public static AgentStrings English { get; } = new EnglishAgentStrings();
    public static AgentStrings Russian { get; } = new RussianAgentStrings();

    public static AgentStrings For(AgentLanguage language) => language switch
    {
        AgentLanguage.Russian => Russian,
        _ => English
    };

    /// <summary>Culture used for clock times, weekday names, and numbers.</summary>
    public abstract CultureInfo Culture { get; }

    public abstract AgentLanguage Language { get; }

    // ---------------------------------------------------------------- units and durations

    /// <summary>"2h" - a whole number of hours.</summary>
    protected abstract string HoursOnly(int hours);
    /// <summary>"1h 20m" - hours with the leading-zero minutes the panels line up on.</summary>
    protected abstract string HoursAndMinutes(int hours, int minutes);
    /// <summary>"45 min" - under an hour, abbreviated.</summary>
    protected abstract string MinutesOnly(int minutes);
    protected abstract string MinuteWord(int count);
    protected abstract string SecondWord(int count);

    /// <summary>Compact duration for badges, rings, and tooltips: "1h 20m" or "45 min".</summary>
    public string DurationLabel(int seconds)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(seconds / 60d));
        return minutes >= 60 ? HoursAndMinutes(minutes / 60, minutes % 60) : MinutesOnly(minutes);
    }

    /// <summary>Spelled-out duration for sentences: "1h 20m" or "45 minutes".</summary>
    public string DurationWords(int seconds)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(seconds / 60d));
        return minutes >= 60
            ? HoursAndMinutes(minutes / 60, minutes % 60)
            : $"{minutes} {MinuteWord(minutes)}";
    }

    /// <summary>How long is left before something closes: "2 minutes" or "45 seconds".</summary>
    public string Countdown(int seconds)
    {
        if (seconds < 60) return $"{seconds} {SecondWord(seconds)}";
        var minutes = (int)Math.Ceiling(seconds / 60d);
        return $"{minutes} {MinuteWord(minutes)}";
    }

    /// <summary>A configured daily allowance, or the words for having none.</summary>
    public string LimitText(int? seconds) => seconds is not int value
        ? NoLimit
        : value % 3600 == 0
            ? HoursOnly(value / 3600)
            : HoursAndMinutes(value / 3600, (value % 3600) / 60);

    /// <summary>A wall-clock deadline: "today at 20:00" or "Fri at 20:00".</summary>
    public string Deadline(DateTimeOffset deadline)
    {
        var local = deadline.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? TodayAt(local.ToString("HH:mm", Culture))
            : WeekdayAt(local.ToString("ddd", Culture), local.ToString("HH:mm", Culture));
    }

    /// <summary>How long ago something last happened, for the connection panel.</summary>
    public string Relative(DateTimeOffset? time)
    {
        if (time is null) return NotYet;
        var elapsed = DateTimeOffset.UtcNow - time.Value;
        if (elapsed < TimeSpan.FromMinutes(1)) return JustNow;
        if (elapsed < TimeSpan.FromHours(1)) return MinutesAgo(Math.Max(1, (int)elapsed.TotalMinutes));
        if (elapsed < TimeSpan.FromDays(1)) return HoursAgo(Math.Max(1, (int)elapsed.TotalHours));
        return time.Value.ToLocalTime().ToString("ddd HH:mm", Culture);
    }

    protected abstract string TodayAt(string time);
    protected abstract string WeekdayAt(string weekday, string time);
    public abstract string NotYet { get; }
    public abstract string JustNow { get; }
    protected abstract string MinutesAgo(int minutes);
    protected abstract string HoursAgo(int hours);

    // ---------------------------------------------------------------- rule decisions

    public abstract string Allowed { get; }
    public abstract string PcScopeName { get; }
    public abstract string NoLimit { get; }

    public abstract string DeviceBlockedByParent { get; }
    public abstract string DeviceTemporarilyBlocked { get; }
    public abstract string DeviceDailyLimitReached { get; }
    public abstract string DeviceOutsideSchedule { get; }

    public abstract string ApplicationBlockedByParent(string application);
    public abstract string ApplicationDailyLimitReached(string application);
    public abstract string ApplicationOutsideSchedule(string application);

    /// <summary>The clause an urgent toast leads with; it must survive being read in one glance.</summary>
    public abstract string ShortReasonManualBlock { get; }
    public abstract string ShortReasonDailyLimit { get; }
    public abstract string ShortReasonOutsideSchedule { get; }
    public abstract string ShortReasonUnavailable { get; }

    // ---------------------------------------------------------------- notifications

    public abstract string SignOutCountdownTitle(int seconds);
    public abstract string ApplicationClosingTitle(string application, int seconds);
    public abstract string SaveYourWorkNow(string shortReason);

    public abstract string PcAvailableTitle { get; }
    public abstract string PcAvailableMessage(string previousShortReason);

    public abstract string UpdatedTitle { get; }
    public abstract string UpdatedMessage(string version);

    public abstract string PcLimitChangedTitle { get; }
    public abstract string ApplicationLimitChangedTitle(string application);
    protected abstract string LimitChangeDailyClause(string limitText);
    protected abstract string LimitChangeScheduleClause { get; }
    protected abstract string LimitChangeSentence(string scope, string clauses);

    /// <summary>"PC: daily time is now 2h, the schedule changed."</summary>
    public string LimitChangeMessage(string scope, int? newDailyLimitSeconds, bool dailyChanged, bool scheduleChanged)
    {
        var clauses = new List<string>(2);
        if (dailyChanged) clauses.Add(LimitChangeDailyClause(LimitText(newDailyLimitSeconds)));
        if (scheduleChanged) clauses.Add(LimitChangeScheduleClause);
        return LimitChangeSentence(scope, string.Join(", ", clauses));
    }

    public abstract string PcTimeLeftTitle(int thresholdSeconds);
    public abstract string ApplicationTimeLeftTitle(string application, int thresholdSeconds);
    public abstract string PcTimeLeftMessage { get; }
    public abstract string ApplicationTimeLeftMessage(string application);

    public abstract string ApplicationTimeTitle(string application);
    public abstract string ApplicationRemainingUntil(string remaining, string deadline);
    public abstract string ApplicationRemainingToday(string remaining);
    public abstract string ApplicationAvailableUntil(string deadline);
    public abstract string ApplicationTimeLimited { get; }

    // ---------------------------------------------------------------- connection

    public abstract string ConnectingToServer { get; }
    public abstract string DeviceNotEnrolled { get; }
    public abstract string Connected { get; }
    public abstract string OfflineCachedRules { get; }

    // ---------------------------------------------------------------- removal

    public abstract string RemovalEnterCredentials { get; }
    public abstract string RemovalAlreadyChecking { get; }
    public abstract string RemovalAlreadyInProgress { get; }
    public abstract string RemovalCredentialsIncorrect { get; }
    public abstract string RemovalAccepted { get; }
    public abstract string RemovalServerUnreachable { get; }
    public abstract string RemovalWindowsFailed { get; }
    public abstract string RemovalServiceSilent { get; }
    public abstract string RemovalDialogFailed { get; }

    // ---------------------------------------------------------------- tray

    public abstract string TrayOpen { get; }
    public abstract string TrayServerLabel { get; }
    public abstract string TraySyncLabel { get; }
    public abstract string TrayProfileLabel { get; }
    public abstract string TrayConnectingToService { get; }
    public abstract string TrayWaitingForService { get; }
    public abstract string TrayLoading { get; }
    public abstract string TraySyncFailed(string lastSuccess);
    public abstract string TrayTooltipConnecting { get; }
    public abstract string TrayTooltipRemaining(string duration);
    public abstract string TrayTooltipAvailable { get; }
    public abstract string TrayTooltipUnavailable { get; }

    /// <summary>One tray menu row, label and value separated the same way in every language.</summary>
    public string TrayRow(string label, string value) => $"{label}  ·  {value}";

    // ---------------------------------------------------------------- screen-time window

    public abstract string HeadlineToday { get; }
    public abstract string HeadlineConnecting { get; }
    public abstract string HeadlineSignedInAs(string profile);
    public abstract string HeadlineLiveView { get; }
    public abstract string BadgeConnecting { get; }
    public abstract string BadgeAvailableNow { get; }
    public abstract string BadgeUnavailable { get; }

    public abstract string TabToday { get; }
    public abstract string TabApps { get; }
    public abstract string TabInternet { get; }
    public abstract string TabConnection { get; }
    public abstract string TabAbout { get; }

    public abstract string DailyScreenTime { get; }
    public abstract string WaitingForService { get; }
    public abstract string LoadingTime { get; }
    public abstract string LeftToday { get; }
    public abstract string Unlimited { get; }
    public abstract string NoDailyLimitCaption { get; }
    public abstract string UsedOf(string used, string total);
    public abstract string UsedToday(string used);

    public abstract string WeeklyScheduleCaption { get; }
    public abstract string LoadingSchedule { get; }
    public abstract string SchedulePlaceholder { get; }
    public abstract string ScreenTimeUnavailableTitle { get; }

    public abstract string NoAppLimitsTitle { get; }
    public abstract string NoAppLimitsDetail { get; }

    public abstract string ServerCaption { get; }
    public abstract string SyncCaption { get; }
    public abstract string ProfileCaption { get; }
    public abstract string Offline { get; }
    public abstract string NoContactYet { get; }
    public abstract string ContactRelative(string relative);
    public abstract string CachedRulesActive { get; }
    public abstract string SyncNeedsAttention { get; }
    public abstract string Waiting { get; }
    public abstract string NoSuccessfulSyncYet { get; }
    public abstract string RulesSynchronized { get; }
    public abstract string NotSelected { get; }
    public abstract string RuleRevisionPlaceholder { get; }
    public abstract string CachedRuleRevision(long revision);
    public abstract string WaitingForLiveStatus { get; }
    public abstract string LiveStatusUpdated(string time);

    public abstract string VersionCaption { get; }
    public abstract string VersionWithNumber(string version);
    public abstract string UpdatesInBackground { get; }
    public abstract string WhatKidTimeSees { get; }
    public abstract string WhatKidTimeSeesDetail { get; }
    public abstract string WhatKidTimeNeverSees { get; }
    public abstract string WhatKidTimeNeverSeesDetail { get; }

    public abstract string RemoveCardTitle { get; }
    public abstract string RemoveCardDetail { get; }
    public abstract string RemoveButton { get; }
    public abstract string RemoveDialogTitle { get; }
    public abstract string RemoveDialogIntro { get; }
    public abstract string ParentEmail { get; }
    public abstract string ParentPassword { get; }
    public abstract string VerifyAndRemove { get; }
    public abstract string Cancel { get; }
    public abstract string CheckingParentAccount { get; }
    public abstract string CheckingParentAccountDetail { get; }
    public abstract string RemovalStarted { get; }
    public abstract string RemovalNotDone { get; }

    public abstract string AppStatusBlocked { get; }
    public abstract string AppStatusLimitReached { get; }
    public abstract string AppStatusOutsideSchedule { get; }
    public abstract string AppStatusAvailable { get; }
    public abstract string AppDailySummary(string remaining, string limit);
    public abstract string AppNoDailyLimit { get; }

    public abstract string ScheduleAlwaysAvailable { get; }
    public abstract string ScheduleAvailableUntil(string deadline);
    public abstract string ScheduleAvailableNow { get; }
    public abstract string ScheduleAvailableAgain(string deadline);
    public abstract string ScheduleNoneThisWeek { get; }
    public abstract string ScheduleNoRestriction { get; }
    public abstract string ScheduleRemainsInWindow(string duration);
    public abstract string ScheduleNextWindowBegins(string deadline);
    public abstract string ScheduleCurrentAllows { get; }
    public abstract string ScheduleNoWindowFound { get; }

    // ---------------------------------------------------------------- countdown card

    public abstract string CountdownCardTimeLeft { get; }
    public abstract string CountdownCardDismiss { get; }

    // ---------------------------------------------------------------- extra time

    /// <summary>
    /// Everything around asking a parent for more time. The child reads all of it, so it lives
    /// here like the rest - and the wording stays plain: a request is a question, never a
    /// negotiation, and a refusal is stated without any suggestion that asking again will work.
    /// </summary>
    public abstract string ExtraTimeCardTitle { get; }

    /// <summary>Names what the extra time would be for: the PC, or one application.</summary>
    public abstract string ExtraTimeForScope(string scope);

    public abstract string ExtraTimeChooseHowMuch { get; }

    /// <summary>The amount the slider is on, as the child reads it: "+15 min".</summary>
    public abstract string ExtraTimeAmount(int minutes);

    public abstract string ExtraTimeAskButton { get; }
    public abstract string ExtraTimeSent { get; }
    public abstract string ExtraTimeWaitingForParent { get; }
    public abstract string ExtraTimeGrantedCaption(int minutes);
    public abstract string ExtraTimeDeniedCaption { get; }

    /// <summary>
    /// The answer to asking again after a refusal, while the same stretch of screen time is still
    /// running. It has to say when asking becomes possible again, or it reads as "never".
    /// </summary>
    public abstract string ExtraTimeDeniedUntilNextPeriod { get; }

    /// <summary>Why a request was not accepted. Each is the whole answer the child gets.</summary>
    public abstract string ExtraTimeNotRunningOutYet { get; }
    public abstract string ExtraTimeAlreadyAsked { get; }
    public abstract string ExtraTimeTooManyToday { get; }
    public abstract string ExtraTimeNotPossible { get; }

    public abstract string ExtraTimeApprovedTitle { get; }
    public abstract string ExtraTimeApprovedMessage(string scope, int minutes);
    public abstract string ExtraTimeDeniedTitle { get; }
    public abstract string ExtraTimeDeniedMessage(string scope);

    /// <summary>Shown on the Today panel once extra time is part of the allowance.</summary>
    public abstract string ExtraTimeAddedToday(int minutes);

    // ---------------------------------------------------------------- web filtering

    /// <summary>
    /// The Internet tab. It describes a filter KidTime does not run: the household's DNS server
    /// answers for every device on the network, so the wording is about the home rather than
    /// about this PC, and it never claims KidTime can see where the child went - it cannot, and
    /// saying so plainly is the point of the last card.
    /// </summary>
    public abstract string WebFilterOnTitle { get; }

    public abstract string WebFilterOnDetail { get; }
    public abstract string WebFilterOffTitle { get; }
    public abstract string WebFilterOffDetail { get; }
    public abstract string BadgeFilterOn { get; }
    public abstract string BadgeFilterOff { get; }

    /// <summary>Said when the picture is the last one that arrived rather than a fresh one.</summary>
    public abstract string WebFilterLastChecked(string relative);

    public abstract string WebFilterAlwaysBlocked { get; }
    public abstract string WebFilterNothingBlocked { get; }

    /// <summary>One category of site the filter blocks at every hour of every day.</summary>
    public abstract string FilterCategoryAds { get; }
    public abstract string FilterCategoryTrackers { get; }
    public abstract string FilterCategoryAdult { get; }
    public abstract string FilterCategoryGambling { get; }
    public abstract string FilterCategoryMalware { get; }
    public abstract string FilterCategorySocial { get; }
    public abstract string FilterCategoryOther { get; }

    /// <summary>Names a category the child reads. Unknown kinds fall to the honest "other".</summary>
    public string FilterCategoryName(DnsFilterCategoryKind kind) => kind switch
    {
        DnsFilterCategoryKind.Ads => FilterCategoryAds,
        DnsFilterCategoryKind.Trackers => FilterCategoryTrackers,
        DnsFilterCategoryKind.Adult => FilterCategoryAdult,
        DnsFilterCategoryKind.Gambling => FilterCategoryGambling,
        DnsFilterCategoryKind.Malware => FilterCategoryMalware,
        DnsFilterCategoryKind.Social => FilterCategorySocial,
        _ => FilterCategoryOther
    };

    /// <summary>How many block lists back one category: "2 lists".</summary>
    public abstract string WebFilterListCount(int count);

    public abstract string WebFilterSiteGroups { get; }

    /// <summary>How many addresses a named set of sites covers: "26 sites".</summary>
    public abstract string WebFilterSiteCount(int count);

    public abstract string BadgeSitesBlocked { get; }
    public abstract string BadgeSitesAvailable { get; }

    /// <summary>When a closed set of sites opens again, and when an open one closes.</summary>
    public abstract string WebFilterBackAt(string deadline);
    public abstract string WebFilterClosesAt(string deadline);

    /// <summary>A set with no timetable at all - off until the parent says otherwise.</summary>
    public abstract string WebFilterBlockedAlways { get; }
    public abstract string WebFilterNoScheduleYet { get; }

    /// <summary>One stretch of the week, as the timetable was written: "21:00 - 12:00".</summary>
    public string WebFilterWindow(string start, string end) => $"{start} - {end}";

    public abstract string WebFilterEveryDay { get; }

    /// <summary>Joins a window with the days it runs on: "21:00 - 12:00, every day".</summary>
    public string WebFilterWindowOnDays(string window, string days) => $"{window}, {days}";

    public abstract string WebFilterExplainTitle { get; }
    public abstract string WebFilterExplainDetail { get; }

    /// <summary>
    /// Said once when a page would not open in the child's browser, so that a site the home
    /// network refuses reads as a rule rather than as a broken computer.
    ///
    /// It states what the household blocks and never what was asked for - KidTime does not know
    /// the address and does not want to - so the wording has to stay a description of the rules
    /// and must not be written as if it were about the page the child was on. The whole sentence
    /// is built here rather than at the call site: Russian declines a list of categories after a
    /// colon and agrees a verb with the set of sites it names, neither of which survives being
    /// glued together from fragments in an English sentence shape.
    /// </summary>
    public abstract string WebFilterBlockedTitle { get; }

    /// <param name="page">
    /// How the page failed, which decides only the closing line. A name that did not resolve is
    /// also what a typo looks like, so that one ends by asking the child to check the address. A
    /// security failure ends by telling them not to click past the browser's warning: the same
    /// page appears when a site is genuinely unsafe, KidTime cannot tell the two apart, and an
    /// explanation that left a child readier to dismiss that warning would have done harm no
    /// blocked site was worth.
    /// </param>
    /// <param name="categories">What this home blocks at every hour; may be empty.</param>
    /// <param name="closedSiteGroup">A named set of sites shut right now, or null.</param>
    /// <param name="reopensAt">When that set comes back, already a local clock time, or null.</param>
    public abstract string WebFilterBlockedMessage(
        BrowserPageError page,
        IReadOnlyList<DnsFilterCategoryKind> categories,
        string? closedSiteGroup,
        string? reopensAt);
}
