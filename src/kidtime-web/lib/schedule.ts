import type { ApplicationSummary, DeviceSummary } from "@/lib/types";
import { formatDuration } from "@/lib/format";

/**
 * Schedule and limit wording for the panel.
 *
 * A household that governs the PC by a weekly schedule sees nothing useful in "No limit" repeated
 * down a page, so these helpers put the schedule first and let the daily limit be the smaller
 * fact it usually is. Clock times are rendered in the controlled PC's own timezone: 20:00 has to
 * mean 20:00 to the child, whichever timezone the parent is reading the panel from.
 */

function deviceFormatter(timeZoneId: string, options: Intl.DateTimeFormatOptions) {
  try {
    return new Intl.DateTimeFormat("en-GB", { ...options, timeZone: timeZoneId });
  } catch {
    // An unknown timezone id falls back to the reader's own clock rather than throwing the page.
    return new Intl.DateTimeFormat("en-GB", options);
  }
}

/**
 * A UTC instant as the clock time on the controlled PC, qualified by day when it is not today's:
 * "20:00", "tomorrow 09:00", "Mon 09:00".
 */
export function formatDeviceClock(utc: string, timeZoneId: string) {
  const instant = new Date(utc);
  const time = deviceFormatter(timeZoneId, { hour: "2-digit", minute: "2-digit", hour12: false }).format(instant);
  const day = deviceFormatter(timeZoneId, { year: "numeric", month: "2-digit", day: "2-digit" });
  const on = day.format(instant);
  if (on === day.format(new Date())) return time;
  if (on === day.format(new Date(Date.now() + 86_400_000))) return `tomorrow ${time}`;
  return `${deviceFormatter(timeZoneId, { weekday: "short" }).format(instant)} ${time}`;
}

/** The one schedule fact worth a headline: what is open, and until or from when. */
export function describeSchedule(device: DeviceSummary): { label: string; value: string } {
  const { schedule } = device;
  if (!schedule.configured) return { label: "Schedule", value: "Any time" };
  if (schedule.withinWindow) {
    return schedule.closesAtUtc
      ? { label: "Allowed until", value: formatDeviceClock(schedule.closesAtUtc, device.timeZoneId) }
      : { label: "Schedule", value: "Open" };
  }
  return schedule.opensAtUtc
    ? { label: "Allowed from", value: formatDeviceClock(schedule.opensAtUtc, device.timeZoneId) }
    : { label: "Schedule", value: "Closed" };
}

/** Where the controlled PC's own clock stands in the day, in minutes from its midnight. */
export function deviceMinuteOfDay(timeZoneId: string) {
  const now = deviceFormatter(timeZoneId, { hour: "2-digit", minute: "2-digit", hour12: false }).format(new Date());
  const [hours, minutes] = now.split(":").map(Number);
  return ((hours % 24) * 60 + minutes) % 1440;
}

/** Today's windows as the parent wrote them, or why there are none. */
export function describeTodayWindows(device: DeviceSummary) {
  if (!device.schedule.configured) return null;
  return device.schedule.todayWindows.length
    ? `Today ${device.schedule.todayWindows.join(", ")}`
    : "Nothing scheduled today";
}

/**
 * The rules an application actually carries, shortest first - and nothing at all when it carries
 * none, because a column of "No limit" says less than an empty one.
 */
export function describeApplicationRules(application: ApplicationSummary) {
  const rules = [
    application.scheduleConfigured ? "Schedule" : null,
    application.dailyLimitSeconds ? `${formatDuration(application.dailyLimitSeconds)} daily` : null,
  ].filter(Boolean);
  return rules.length ? rules.join(" · ") : null;
}
