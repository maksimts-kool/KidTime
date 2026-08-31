import { cn } from "@/lib/utils";

/**
 * The day as a bar: allowed windows filled, the rest empty, and a marker where the controlled PC
 * is right now.
 *
 * A weekly schedule is the rule a parent reasons about spatially - "evenings, and Saturday
 * afternoon" - and a list of "08:00–15:00" strings makes them read it back into a shape they
 * already had in mind. Overnight windows are drawn as the two pieces they occupy in one day.
 */
export function ScheduleStrip({
  windows,
  nowMinute,
  className,
}: {
  windows: string[];
  nowMinute?: number | null;
  className?: string;
}) {
  const segments = windows.flatMap(toSegments);
  return (
    <div className={cn("w-full", className)}>
      {/* The marker sits outside the clipped track so it can stand a little proud of it. */}
      <div className="relative py-1">
        <div
          className="relative h-2.5 w-full overflow-hidden rounded-full bg-muted ring-1 ring-border/70 ring-inset"
          role="img"
          aria-label={segments.length ? `Allowed today: ${windows.join(", ")}` : "Nothing scheduled today"}
        >
          {segments.map(segment => (
            <span
              key={`${segment.from}-${segment.to}`}
              className="absolute inset-y-0 rounded-full bg-primary/85"
              style={{ left: `${percent(segment.from)}%`, width: `${percent(segment.to - segment.from)}%` }}
            />
          ))}
        </div>
        {nowMinute != null && (
          <span
            aria-hidden
            className="absolute inset-y-0 w-[3px] -translate-x-1/2 rounded-full bg-foreground/85 ring-2 ring-card"
            style={{ left: `${percent(nowMinute)}%` }}
          />
        )}
      </div>
      <div className="mt-1 flex justify-between text-[10px] text-muted-foreground tabular-nums">
        <span>00</span><span>06</span><span>12</span><span>18</span><span>24</span>
      </div>
    </div>
  );
}

const percent = (minutes: number) => (minutes / 1440) * 100;

/** "08:00–15:00" as minutes from midnight; an overnight window becomes the two pieces it fills. */
function toSegments(window: string) {
  const [from, to] = window.split("–").map(minutesOf);
  if (from == null || to == null) return [];
  if (from === to) return [{ from: 0, to: 1440 }];
  return from < to ? [{ from, to }] : [{ from, to: 1440 }, { from: 0, to }];
}

function minutesOf(value: string) {
  const [hours, minutes] = value.trim().split(":").map(Number);
  if (!Number.isFinite(hours) || !Number.isFinite(minutes)) return null;
  return hours * 60 + minutes;
}
