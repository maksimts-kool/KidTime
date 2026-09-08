import { formatDuration, formatSeen } from "@/lib/format";
import type { ApplicationSummary, DeviceStatistics, DeviceSummary } from "@/lib/types";

/**
 * What this installation would be better for, worked out from what it already knows.
 *
 * A settings page with nothing but a privacy notice on it is a tab nobody opens. These are the
 * things a parent would otherwise have to notice for themselves - a PC enforcing nothing because
 * no account was chosen, a game that took eight hours this week with no rule on it, an agent a
 * release behind - each with the page that fixes it.
 *
 * Nothing here changes anything on its own. A suggestion is a sentence and a link.
 */
export type SuggestionTone = "critical" | "warning" | "info";

export type Suggestion = {
  id: string;
  tone: SuggestionTone;
  title: string;
  detail: string;
  href: string;
  action: string;
};

/** An application used this much in the period with no rule at all is worth mentioning once. */
const unruledApplicationSeconds = 4 * 3600;

/** Below this a PC that has not reported is simply switched off, not a problem to raise. */
const staleDeviceHours = 24;

const toneOrder: Record<SuggestionTone, number> = { critical: 0, warning: 1, info: 2 };

export function buildSuggestions({
  devices,
  applications,
  statistics,
  unresolvedFaults,
  pendingRequests,
}: {
  devices: DeviceSummary[];
  applications: ApplicationSummary[];
  /** Per device, keyed by device id; absent when the range could not be read. */
  statistics: Map<string, DeviceStatistics>;
  unresolvedFaults: number;
  pendingRequests: number;
}): Suggestion[] {
  const suggestions: Suggestion[] = [];

  for (const device of devices) {
    if (!device.controlledUserName) {
      suggestions.push({
        id: `no-account-${device.id}`,
        tone: "critical",
        title: `${device.name} is not enforcing anything`,
        detail: "No Windows account is selected, so screen time is counted for nobody and no rule applies. Choose the child's standard account on the device page.",
        href: `/devices/${device.id}`,
        action: "Choose the account",
      });
    } else if (!device.schedule.configured && device.dailyLimitSeconds == null) {
      suggestions.push({
        id: `no-rules-${device.id}`,
        tone: "warning",
        title: `${device.name} has no schedule and no daily limit`,
        detail: "Applications are still discovered and time is still counted, but nothing closes the PC. A weekly schedule is the rule most households actually govern a PC with.",
        href: `/devices/${device.id}`,
        action: "Set a schedule",
      });
    }

    if (!device.isAgentUpToDate && device.latestAgentVersion) {
      suggestions.push({
        id: `update-${device.id}`,
        tone: "info",
        title: `${device.name} is a version behind`,
        detail: `It reports ${device.agentVersion ?? "an unknown version"} and ${device.latestAgentVersion} has been published. The PC installs it by itself; this is only worth checking if it stays behind.`,
        href: `/devices/${device.id}`,
        action: "Open the device",
      });
    }

    if (!device.isOnline && hoursSince(device.lastSeenUtc) > staleDeviceHours) {
      suggestions.push({
        id: `offline-${device.id}`,
        tone: "warning",
        title: `${device.name} has not reported for a while`,
        detail: `Last seen ${formatSeen(device.lastSeenUtc)}. Cached rules keep being enforced while it is offline, but new rules and usage are waiting for it to reconnect.`,
        href: `/devices/${device.id}`,
        action: "Open the device",
      });
    }

    const heaviest = heaviestUnruledApplication(device, applications, statistics.get(device.id));
    if (heaviest) {
      suggestions.push({
        id: `unruled-${heaviest.application.id}`,
        tone: "info",
        title: `${heaviest.application.displayName} has no rule on it`,
        detail: `${formatDuration(heaviest.activeSeconds)} on ${device.name} over the last week, with no limit, schedule, or block. A daily limit is one field on its page.`,
        href: `/applications/${heaviest.application.id}`,
        action: "Set a limit",
      });
    }
  }

  if (unresolvedFaults > 0) {
    suggestions.push({
      id: "faults",
      tone: "warning",
      title: unresolvedFaults === 1 ? "One fault is waiting in the error log" : `${unresolvedFaults} faults are waiting in the error log`,
      detail: "A controlled PC is usually unreachable, so anything that went wrong on it arrives here instead. Repeats raise a count rather than adding rows.",
      href: "/diagnostics",
      action: "Open the error log",
    });
  }

  if (pendingRequests > 0) {
    suggestions.push({
      id: "requests",
      tone: "critical",
      title: pendingRequests === 1 ? "A child is waiting on an answer" : `${pendingRequests} requests are waiting on an answer`,
      detail: "Extra time was asked for and nothing has been decided. Until it is, the child cannot ask again for that scope.",
      href: "/requests",
      action: "Review requests",
    });
  }

  return suggestions.sort((left, right) => toneOrder[left.tone] - toneOrder[right.tone]);
}

/**
 * The one application on this PC worth naming: the most used of those carrying no rule at all.
 * One per device, because a list of twelve is the same as no suggestion.
 */
function heaviestUnruledApplication(
  device: DeviceSummary,
  applications: ApplicationSummary[],
  statistics: DeviceStatistics | undefined,
) {
  if (!statistics) return null;
  const unruled = new Map(
    applications
      .filter(application => application.deviceId === device.id)
      .filter(application => !application.manuallyBlocked && application.dailyLimitSeconds == null && !application.scheduleConfigured)
      .map(application => [application.displayName.toLowerCase(), application]),
  );
  for (const used of statistics.applications) {
    if (used.activeSeconds < unruledApplicationSeconds) break;
    const application = unruled.get(used.displayName.toLowerCase());
    if (application) return { application, activeSeconds: used.activeSeconds };
  }
  return null;
}

function hoursSince(value: string | null) {
  if (!value) return Number.POSITIVE_INFINITY;
  return (Date.now() - new Date(value).getTime()) / 3_600_000;
}
