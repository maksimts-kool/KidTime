import type { ReactNode } from "react";
import type { WeeklySchedule } from "@/lib/types";
import { ScheduleStrip } from "@/components/schedule-strip";

export const weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

/** A day's ranges as "12:00–15:00" strings, in the order they happen. */
export function windowsOf(schedule: WeeklySchedule, day: string) {
  return (schedule.days.find(item => item.day === day)?.windows ?? [])
    .map(window => ({ start: window.start.slice(0, 5), end: window.end.slice(0, 5) }))
    .sort((left, right) => left.start.localeCompare(right.start))
    .map(window => `${window.start}–${window.end}`);
}

/**
 * A week read at a glance: one bar per day with the allowed ranges filled, and the times written
 * beside it. It only shows - every change is made in a popup the row's action opens - so the card
 * reads as the rule in force rather than as a form half-way through being edited.
 */
export function WeekView({
  schedule,
  dayAction,
}: {
  schedule: WeeklySchedule;
  /** A control at the end of each row, such as the button that edits that day. */
  dayAction?: (day: string) => ReactNode;
}) {
  return (
    <div className="grid gap-1">
      {weekdays.map(day => {
        const windows = windowsOf(schedule, day);
        return (
          <div key={day} className="grid grid-cols-[5.5rem_1fr_auto] items-center gap-x-3 gap-y-1 rounded-lg px-2 py-1.5 hover:bg-muted/50 sm:grid-cols-[6.5rem_minmax(0,1fr)_11rem_auto]">
            <span className="text-sm font-medium">{day}</span>
            <ScheduleStrip
              windows={windows}
              showScale={false}
              label={windows.length ? `${day}: ${windows.join(", ")}` : `${day}: closed`}
              className="col-span-2 row-start-2 sm:col-span-1 sm:row-start-auto"
            />
            <span className={windows.length ? "text-right text-sm tabular-nums sm:text-left" : "text-right text-sm text-muted-foreground sm:text-left"}>
              {/* A line may break between ranges, never inside one - "18:00–" over "22:00" reads as two times. */}
              {windows.length
                ? windows.map((window, index) => <span key={window} className="whitespace-nowrap">{window}{index < windows.length - 1 ? ", " : ""}</span>)
                : "Closed"}
            </span>
            <span className="row-start-1 col-start-3 justify-self-end sm:row-start-auto sm:col-start-auto">{dayAction?.(day)}</span>
          </div>
        );
      })}
      <div className="hidden grid-cols-[6.5rem_minmax(0,1fr)_11rem_auto] gap-x-3 px-2 sm:grid">
        <span />
        <div className="flex justify-between text-[10px] text-muted-foreground tabular-nums">
          <span>00</span><span>06</span><span>12</span><span>18</span><span>24</span>
        </div>
      </div>
    </div>
  );
}
