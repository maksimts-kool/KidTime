import type { ApplicationSummary, DeviceSummary } from "@/lib/types";
import { formatDuration } from "@/lib/format";

/**
 * Schedule and limit wording for the panel.
 *
 * A household that governs the PC by a weekly schedule sees nothing useful in "No limit" repeated
 * down a page, so these helpers put the schedule first and let the daily limit be the smaller fact
 * it usually is.
 *
 * Every time here is already the controlled PC's own wall clock, computed by the server: a Windows
 * PC reports a Windows timezone id ("Russian Standard Time") which `Intl` cannot resolve, and
 * quietly renders UTC instead - a window closing at 21:00 was shown to the parent as 18:00. So
 * nothing in the panel converts timezones; it only reads the stamps it was given.
 */

/** A device-local stamp, "2026-08-31T21:00", as "21:00" / "tomorrow 09:00" / "Mon 09:00". */
export function formatDeviceClock(stamp: string, localDate: string) {
  const [date, time] = stamp.split("T");
  if (!time) return stamp;
  if (date === localDate) return time;
  if (date === addDays(localDate, 1)) return `tomorrow ${time}`;
  return `${weekdayOf(date)} ${time}`;
}

/** Dates are compared as plain calendar days, so UTC is only a way to avoid a timezone at all. */
function addDays(date: string, days: number) {
  const shifted = new Date(`${date}T00:00:00Z`);
  shifted.setUTCDate(shifted.getUTCDate() + days);
  return shifted.toISOString().slice(0, 10);
}

function weekdayOf(date: string) {
  return new Intl.DateTimeFormat("en-GB", { weekday: "short", timeZone: "UTC" })
    .format(new Date(`${date}T00:00:00Z`));
}

/** The one schedule fact worth a headline: what is open, and until or from when. */
export function describeSchedule(device: DeviceSummary): { label: string; value: string } {
  const { schedule } = device;
  if (!schedule.configured) return { label: "Schedule", value: "Any time" };
  if (schedule.withinWindow) {
    return schedule.closesAtLocal
      ? { label: "Allowed until", value: formatDeviceClock(schedule.closesAtLocal, schedule.localDate) }
      : { label: "Schedule", value: "Open" };
  }
  return schedule.opensAtLocal
    ? { label: "Allowed from", value: formatDeviceClock(schedule.opensAtLocal, schedule.localDate) }
    : { label: "Schedule", value: "Closed" };
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
