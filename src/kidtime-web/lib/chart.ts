import type { DailyUsage } from "@/lib/types";

/**
 * Chart wording and colour roles for the panel.
 *
 * Two rules hold everywhere here. A chart with one series wears the brand primary, because a
 * second colour would mean a second thing and there isn't one. A chart with several wears
 * `seriesColor`, whose order was validated for colour-vision deficiency against the white card
 * and must not be re-ordered casually - and because three of those sit under 3:1 on white, every
 * chart that uses them ships a written breakdown beside it rather than leaving colour to carry
 * the value alone.
 */

/** The categorical slots, in the order they were validated in. */
export const seriesColors = [
  "var(--chart-1)",
  "var(--chart-2)",
  "var(--chart-3)",
  "var(--chart-4)",
  "var(--chart-5)",
  "var(--chart-6)",
] as const;

export const otherSeriesColor = "var(--chart-other)";

/** Colour follows the entity, never its rank, so the caller passes the position it was given. */
export function seriesColor(index: number) {
  return index < seriesColors.length ? seriesColors[index] : otherSeriesColor;
}

/** Axis ticks: short enough to sit in a 44px gutter, and never "0h 0m". */
export function formatAxisDuration(seconds: number) {
  if (seconds <= 0) return "0";
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
  const hours = seconds / 3600;
  return `${hours % 1 === 0 ? hours : hours.toFixed(1)}h`;
}

/**
 * A device-local calendar day, read as a plain date. Noon avoids the day slipping either way
 * when the browser's own offset is applied - the dates in these payloads are the PC's own days,
 * not instants, and nothing in the panel converts a timezone.
 */
export function parseLocalDate(date: string) {
  return new Date(`${date}T12:00:00`);
}

/**
 * The panel is written in English and says so; the child's PC is the half that is translated.
 * Naming the locale rather than taking the reader's is also what keeps a chart drawn on the
 * server and hydrated in the browser from disagreeing about what to call a Wednesday.
 */
const locale = "en-GB";

export function formatWeekday(date: string) {
  return parseLocalDate(date).toLocaleDateString(locale, { weekday: "short" });
}

export function formatDayLabel(date: string) {
  return parseLocalDate(date).toLocaleDateString(locale, { day: "numeric", month: "short" });
}

export function formatFullDate(date: string) {
  return parseLocalDate(date).toLocaleDateString(locale, {
    weekday: "long",
    day: "numeric",
    month: "long",
  });
}

/**
 * Axis ticks on whole hours (or half-hours for a short day), so a reader lands on "2h" rather
 * than "2.3h". Returns the tick values and the domain top they imply.
 */
const tickSteps = [900, 1800, 3600, 2 * 3600, 3 * 3600, 4 * 3600, 6 * 3600, 8 * 3600, 12 * 3600];

export function durationTicks(peakSeconds: number) {
  const step = tickSteps.find(candidate => peakSeconds / candidate <= 4) ?? tickSteps[tickSteps.length - 1];
  const top = Math.max(step, Math.ceil(peakSeconds / step) * step);
  const ticks: number[] = [];
  for (let value = 0; value <= top; value += step) ticks.push(value);
  return { ticks, top };
}

/** The share of a total, as a rounded percentage that never reads "0%" for a real value. */
export function share(part: number, total: number) {
  if (total <= 0 || part <= 0) return 0;
  return Math.max(1, Math.round((part / total) * 100));
}

/**
 * How this period compares with the one before it. Null when there is nothing to compare
 * against - a first week has no trend, and "+100%" would be an invention.
 */
export function changeAgainst(current: number, previous: number): number | null {
  if (previous <= 0) return null;
  return Math.round(((current - previous) / previous) * 100);
}

/** The busiest day in a series, or null when nothing was recorded at all. */
export function busiestDay(daily: DailyUsage[]): DailyUsage | null {
  const best = daily.reduce<DailyUsage | null>(
    (top, day) => (day.activeSeconds > (top?.activeSeconds ?? 0) ? day : top),
    null,
  );
  return best && best.activeSeconds > 0 ? best : null;
}
