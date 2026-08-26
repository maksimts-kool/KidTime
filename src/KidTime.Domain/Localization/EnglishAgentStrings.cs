using System.Globalization;

namespace KidTime.Domain.Localization;

/// <summary>The default language, and the wording every other translation is measured against.</summary>
internal sealed class EnglishAgentStrings : AgentStrings
{
    public override CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");
    public override AgentLanguage Language => AgentLanguage.English;

    protected override string HoursOnly(int hours) => $"{hours}h";
    protected override string HoursAndMinutes(int hours, int minutes) => $"{hours}h {minutes:00}m";
    protected override string MinutesOnly(int minutes) => $"{minutes} min";
    protected override string MinuteWord(int count) => count == 1 ? "minute" : "minutes";
    protected override string SecondWord(int count) => count == 1 ? "second" : "seconds";

    protected override string TodayAt(string time) => $"today at {time}";
    protected override string WeekdayAt(string weekday, string time) => $"{weekday} at {time}";
    public override string NotYet => "Not yet";
    public override string JustNow => "Just now";
    protected override string MinutesAgo(int minutes) => $"{minutes} min ago";
    protected override string HoursAgo(int hours) => $"{hours} h ago";

    public override string Allowed => "Allowed";
    public override string PcScopeName => "PC";
    public override string NoLimit => "no limit";

    public override string DeviceBlockedByParent => "This PC was blocked by your parent.";
    public override string DeviceTemporarilyBlocked => "This PC is temporarily blocked.";
    public override string DeviceDailyLimitReached => "Today's PC time limit has been reached.";
    public override string DeviceOutsideSchedule => "PC use is not allowed at this time.";

    public override string ApplicationBlockedByParent(string application) =>
        $"{application} is blocked by your parent.";
    public override string ApplicationDailyLimitReached(string application) =>
        $"{application}'s daily time limit has been reached.";
    public override string ApplicationOutsideSchedule(string application) =>
        $"{application} is not available at this time.";

    public override string ShortReasonManualBlock => "Your parent blocked it";
    public override string ShortReasonDailyLimit => "Daily time is used up";
    public override string ShortReasonOutsideSchedule => "Outside allowed hours";
    public override string ShortReasonUnavailable => "Not available right now";

    public override string SignOutCountdownTitle(int seconds) => $"Signing out in {Countdown(seconds)}";
    public override string ApplicationClosingTitle(string application, int seconds) =>
        $"{application} closes in {Countdown(seconds)}";
    public override string SaveYourWorkNow(string shortReason) => $"{shortReason}. Save your work now.";

    public override string PcAvailableTitle => "PC available";
    public override string PcAvailableMessage(string previousShortReason) =>
        $"You can use this PC again. Earlier: {previousShortReason.ToLowerInvariant()}.";

    public override string UpdatedTitle => "KidTime updated";
    public override string UpdatedMessage(string version) =>
        $"KidTime is now version {version}. Nothing changes for you.";

    public override string PcLimitChangedTitle => "PC time limit changed";
    public override string ApplicationLimitChangedTitle(string application) => $"{application} time limit changed";
    protected override string LimitChangeDailyClause(string limitText) => $"daily time is now {limitText}";
    protected override string LimitChangeScheduleClause => "the schedule changed";
    protected override string LimitChangeSentence(string scope, string clauses) => $"{scope}: {clauses}.";

    public override string PcTimeLeftTitle(int thresholdSeconds) =>
        $"{Countdown(thresholdSeconds)} of PC time left";
    public override string ApplicationTimeLeftTitle(string application, int thresholdSeconds) =>
        $"{Countdown(thresholdSeconds)} of {application} left";
    public override string PcTimeLeftMessage => "Windows signs you out when it runs out.";
    public override string ApplicationTimeLeftMessage(string application) =>
        $"{application} closes when it runs out.";

    public override string ApplicationTimeTitle(string application) => $"{application} time";
    public override string ApplicationRemainingUntil(string remaining, string deadline) =>
        $"{remaining} left, until {deadline}.";
    public override string ApplicationRemainingToday(string remaining) => $"{remaining} left today.";
    public override string ApplicationAvailableUntil(string deadline) => $"Available until {deadline}.";
    public override string ApplicationTimeLimited => "Time limited.";

    public override string ConnectingToServer => "Connecting to server";
    public override string DeviceNotEnrolled => "Device is not enrolled";
    public override string Connected => "Connected";
    public override string OfflineCachedRules => "Offline - cached rules active";

    public override string RemovalEnterCredentials => "Enter the parent email address and password.";
    public override string RemovalAlreadyChecking => "KidTime is already checking a removal request.";
    public override string RemovalAlreadyInProgress => "KidTime removal is already in progress.";
    public override string RemovalCredentialsIncorrect => "The parent email address or password is incorrect.";
    public override string RemovalAccepted => "Parent account verified. KidTime is being removed from this PC.";
    public override string RemovalServerUnreachable =>
        "KidTime could not verify the parent login with the server. Check the connection and try again.";
    public override string RemovalWindowsFailed =>
        "The parent login was accepted, but Windows could not start KidTime removal. Try again.";
    public override string RemovalServiceSilent => "The KidTime service did not answer. Wait a moment and try again.";
    public override string RemovalDialogFailed => "Something went wrong. Try again in a moment.";

    public override string TrayOpen => "Open KidTime";
    public override string TrayServerLabel => "Server";
    public override string TraySyncLabel => "Synchronization";
    public override string TrayProfileLabel => "Controlled profile";
    public override string TrayConnectingToService => "Connecting to service";
    public override string TrayWaitingForService => "Waiting for service";
    public override string TrayLoading => "Loading";
    public override string TraySyncFailed(string lastSuccess) => $"Failed (last success {lastSuccess})";
    public override string TrayTooltipConnecting => "KidTime - connecting to service";
    public override string TrayTooltipRemaining(string duration) => $"KidTime - {duration} left today";
    public override string TrayTooltipAvailable => "KidTime - screen time available";
    public override string TrayTooltipUnavailable => "KidTime - screen time unavailable";

    public override string HeadlineToday => "Your time today";
    public override string HeadlineConnecting => "Connecting to the KidTime service";
    public override string HeadlineSignedInAs(string profile) => $"Signed in as {profile}";
    public override string HeadlineLiveView => "A live view of screen time, schedules, and controlled apps.";
    public override string BadgeConnecting => "Connecting";
    public override string BadgeAvailableNow => "Available now";
    public override string BadgeUnavailable => "Unavailable";

    public override string TabToday => "Today";
    public override string TabApps => "Apps";
    public override string TabConnection => "Connection";
    public override string TabAbout => "About";

    public override string DailyScreenTime => "Daily screen time";
    public override string WaitingForService => "Waiting for the service...";
    public override string LoadingTime => "loading time";
    public override string LeftToday => "left today";
    public override string Unlimited => "Unlimited";
    public override string NoDailyLimitCaption => "no daily limit";
    public override string UsedOf(string used, string total) => $"{used} used of {total}";
    public override string UsedToday(string used) => $"{used} used today";

    public override string WeeklyScheduleCaption => "WEEKLY SCHEDULE";
    public override string LoadingSchedule => "Loading schedule";
    public override string SchedulePlaceholder => "Schedule information will appear here.";
    public override string ScreenTimeUnavailableTitle => "Screen time unavailable";

    public override string NoAppLimitsTitle => "No app limits or blocks";
    public override string NoAppLimitsDetail => "Only apps your parent limits appear in this list.";

    public override string ServerCaption => "SERVER";
    public override string SyncCaption => "SYNCHRONIZATION";
    public override string ProfileCaption => "CONTROLLED PROFILE";
    public override string Offline => "Offline";
    public override string NoContactYet => "No contact yet";
    public override string ContactRelative(string relative) => $"Contact {relative}";
    public override string CachedRulesActive => "Cached rules active";
    public override string SyncNeedsAttention => "Sync needs attention";
    public override string Waiting => "Waiting";
    public override string NoSuccessfulSyncYet => "No successful synchronization yet";
    public override string RulesSynchronized => "Rules and usage are synchronized";
    public override string NotSelected => "Not selected";
    public override string RuleRevisionPlaceholder => "Rule revision --";
    public override string CachedRuleRevision(long revision) => $"Cached rule revision {revision}";
    public override string WaitingForLiveStatus => "Waiting for live status";
    public override string LiveStatusUpdated(string time) => $"Live status updated {time}";

    public override string VersionCaption => "Version";
    public override string VersionWithNumber(string version) => $"Version {version}";
    public override string UpdatesInBackground => "KidTime updates itself in the background.";
    public override string WhatKidTimeSees => "What KidTime sees";
    public override string WhatKidTimeSeesDetail =>
        "Active time on this PC and which app is in front. That is all.";
    public override string WhatKidTimeNeverSees => "What KidTime never sees";
    public override string WhatKidTimeNeverSeesDetail =>
        "Websites, searches, messages, keystrokes, your screen, your camera, or your microphone.";

    public override string RemoveCardTitle => "Remove KidTime from this PC";
    public override string RemoveCardDetail =>
        "A parent must sign in online before the Windows service, local rules, usage data, and program files can be removed.";
    public override string RemoveButton => "Remove KidTime";
    public override string RemoveDialogTitle => "Remove KidTime from this PC?";
    public override string RemoveDialogIntro =>
        "This permanently removes KidTime services and local data. The server must be reachable to verify the parent account.";
    public override string ParentEmail => "Parent email address";
    public override string ParentPassword => "Parent password";
    public override string VerifyAndRemove => "Verify and remove";
    public override string Cancel => "Cancel";
    public override string CheckingParentAccount => "Checking parent account";
    public override string CheckingParentAccountDetail => "KidTime is securely verifying the login with the server.";
    public override string RemovalStarted => "Removal started";
    public override string RemovalNotDone => "KidTime was not removed";

    public override string AppStatusBlocked => "Blocked";
    public override string AppStatusLimitReached => "Limit reached";
    public override string AppStatusOutsideSchedule => "Outside schedule";
    public override string AppStatusAvailable => "Available";
    public override string AppDailySummary(string remaining, string limit) => $"{remaining} left of {limit} daily";
    public override string AppNoDailyLimit => "No daily limit";

    public override string ScheduleAlwaysAvailable => "Available at any time";
    public override string ScheduleAvailableUntil(string deadline) => $"Available until {deadline}";
    public override string ScheduleAvailableNow => "Available now";
    public override string ScheduleAvailableAgain(string deadline) => $"Available again {deadline}";
    public override string ScheduleNoneThisWeek => "Not available this week";
    public override string ScheduleNoRestriction => "No weekly schedule restricts screen time.";
    public override string ScheduleRemainsInWindow(string duration) =>
        $"{duration} remains in the current schedule window.";
    public override string ScheduleNextWindowBegins(string deadline) =>
        $"The next allowed schedule window begins {deadline}.";
    public override string ScheduleCurrentAllows => "The current schedule allows screen time.";
    public override string ScheduleNoWindowFound => "No allowed schedule window was found for this week.";

    public override string CountdownCardTimeLeft => "Time left";
    public override string CountdownCardDismiss => "Got it";
}
