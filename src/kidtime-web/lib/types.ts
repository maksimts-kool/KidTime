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
  closesAtUtc: string | null;
  opensAtUtc: string | null;
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
export type DeviceStatistics = {
  from: string;
  to: string;
  totalActiveSeconds: number;
  daily: DailyUsage[];
  applications: { displayName: string; identityKey: string; activeSeconds: number }[];
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
