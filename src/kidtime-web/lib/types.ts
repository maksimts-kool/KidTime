/** The language the controlled PC shows notifications and its tray dashboard in. */
export type AgentLanguage = "English" | "Russian";

export const agentLanguages: { value: AgentLanguage; label: string }[] = [
  { value: "English", label: "English" },
  { value: "Russian", label: "Russian (Русский)" },
];

/**
 * What the weekly schedule means right now, as the server worked it out in the device's own
 * timezone. Instants are UTC; the panel renders them in the child's clock time.
 */
export type ScheduleState = {
  configured: boolean;
  withinWindow: boolean;
  /** The device's own calendar day, "2026-08-31". */
  localDate: string;
  /** Where the device's clock stands in that day, in minutes from its midnight. */
  nowMinuteOfDay: number;
  /** Device-local stamps, "2026-08-31T21:00" - never instants; see lib/schedule.ts. */
  closesAtLocal: string | null;
  opensAtLocal: string | null;
  todayWindows: string[];
};

export type DeviceSummary = {
  id: string;
  name: string;
  windowsVersion: string;
  timeZoneId: string;
  isOnline: boolean;
  lastSeenUtc: string | null;
  loggedInUser: string | null;
  foregroundApplication: string | null;
  controlledUserName: string | null;
  language: AgentLanguage;
  agentVersion: string | null;
  latestAgentVersion: string | null;
  agentUpdateStatus: string | null;
  agentUpdateError: string | null;
  agentUpdateCheckedAtUtc: string | null;
  isAgentUpToDate: boolean;
  todayActiveSeconds: number;
  dailyLimitSeconds: number | null;
  remainingSeconds: number | null;
  schedule: ScheduleState;
  manuallyBlocked: boolean;
  manualBlockUntilUtc: string | null;
  ruleRevision: number;
  appliedRuleRevision: number;
  unresolvedFaults: number;
};

export type DiagnosticEvent = {
  id: string;
  deviceId: string;
  deviceName: string;
  component: string;
  severity: string;
  message: string;
  exceptionType: string | null;
  detail: string | null;
  agentVersion: string | null;
  occurrenceCount: number;
  firstOccurredAtUtc: string;
  lastOccurredAtUtc: string;
  resolvedAtUtc: string | null;
};

export type TimeWindow = { start: string; end: string };
export type DaySchedule = { day: string; windows: TimeWindow[] };
export type WeeklySchedule = { days: DaySchedule[] };

export type DeviceRule = {
  deviceId: string;
  revision: number;
  timeZoneId: string;
  language: AgentLanguage;
  controlledUserSid: string | null;
  controlledUserName: string | null;
  idleThresholdSeconds: number;
  manuallyBlocked: boolean;
  manualBlockUntilUtc: string | null;
  dailyLimitSeconds: number | null;
  schedule: WeeklySchedule;
};

export type WindowsUserAccount = {
  sid: string;
  accountName: string;
  displayName: string;
  isEnabled: boolean;
  isAdministrator: boolean;
};

export type DeviceDetail = {
  device: {
    id: string;
    name: string;
    windowsVersion: string;
    timeZoneId: string;
    enrolledAtUtc: string;
    lastSeenUtc: string | null;
    loggedInUser: string | null;
    foregroundApplication: string | null;
    agentVersion: string | null;
    latestAgentVersion: string | null;
    agentUpdateStatus: string | null;
    agentUpdateError: string | null;
    agentUpdateCheckedAtUtc: string | null;
    isAgentUpToDate: boolean;
    appliedRuleRevision: number;
    isOnline: boolean;
  };
  rules: DeviceRule;
  availableWindowsUsers: WindowsUserAccount[];
};

export type ApplicationSummary = {
  id: string;
  deviceId: string;
  deviceName: string;
  identityKey: string;
  displayName: string;
  executableName: string;
  executablePath: string;
  fileVersion: string | null;
  publisher: string | null;
  firstSeenUtc: string;
  lastSeenUtc: string;
  todayActiveSeconds: number;
  hasIcon: boolean;
  isMicrosoft: boolean;
  manuallyBlocked: boolean;
  dailyLimitSeconds: number | null;
  scheduleConfigured: boolean;
  withinSchedule: boolean;
};

export type DailyUsage = { date: string; activeSeconds: number };

/** One application's share of a statistics period, with the days it was spent across. */
export type StatisticsApplication = {
  identityKey: string;
  /** The row an icon and a rule page are addressed by. */
  deviceApplicationId: string;
  displayName: string;
  publisher: string | null;
  hasIcon: boolean;
  activeSeconds: number;
  /** The same stretch of days immediately before this one, so a total can be judged. */
  previousActiveSeconds: number;
  daily: DailyUsage[];
};

export type DeviceStatistics = {
  from: string;
  to: string;
  /** Days in the range, including the ones with nothing on them. */
  days: number;
  totalActiveSeconds: number;
  previousTotalActiveSeconds: number;
  dailyAverageSeconds: number;
  /** How many applications the server expects to be given a colour of their own. */
  chartedApplications: number;
  daily: DailyUsage[];
  applications: StatisticsApplication[];
};

/** One child's request for extra time, and what was decided about it. */
export type TimeExtensionRequest = {
  id: string;
  deviceId: string;
  deviceName: string;
  displayName: string;
  applicationIdentityKey: string | null;
  deviceApplicationId: string | null;
  isPc: boolean;
  localDate: string;
  requestedMinutes: number;
  grantedMinutes: number;
  status: "Pending" | "Approved" | "Denied";
  requestedAtUtc: string;
  decidedAtUtc: string | null;
};

/**
 * What the household's DNS filter is doing, read by the server from the Technitium DNS server the
 * parent runs. The panel only reports it and hands the parent over to that server's own console:
 * KidTime does not filter the web and holds no write access to anything that does.
 */
export type DnsFilteringState = "NotConfigured" | "Unreachable" | "Inactive" | "Active";

export type DnsFilterCategoryKind =
  | "Ads"
  | "Trackers"
  | "Adult"
  | "Gambling"
  | "Malware"
  | "Social"
  | "Other";

export type DnsFiltering = {
  /** The DNS console's address as the parent's browser reaches it, or null when there is none. */
  consoleUrl: string | null;
  state: DnsFilteringState;
  groupName: string | null;
  retrievedAtUtc: string | null;
  /** The last read failed; everything else here is the answer before it. */
  isStale: boolean;
  categories: { kind: DnsFilterCategoryKind; listCount: number }[];
  siteGroups: {
    name: string;
    siteCount: number;
    isBlockedNow: boolean;
    changesAtUtc: string | null;
    /** Wall-clock in the timezone the rule was written in; days are 0 = Sunday. */
    windows: { startTime: string; endTime: string; days: number[] }[];
  }[];
};
