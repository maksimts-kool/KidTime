"use client";

import { Plus, Trash2 } from "lucide-react";
import type { WeeklySchedule } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Separator } from "@/components/ui/separator";
import { Switch } from "@/components/ui/switch";

const weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

export type EditableWindow = { id: string; start: string; end: string };
export type EditableDay = { day: string; enabled: boolean; windows: EditableWindow[] };

export function scheduleToEditable(schedule: WeeklySchedule): EditableDay[] {
  return weekdays.map(day => {
    const windows = schedule.days.find(item => item.day === day)?.windows ?? [];
    return {
      day,
      enabled: windows.length > 0,
      windows: windows.length > 0
        ? windows.map((window, index) => ({ id: `${day}-${index}`, start: window.start.slice(0, 5), end: window.end.slice(0, 5) }))
        : [{ id: `${day}-0`, start: "07:00", end: "22:00" }],
    };
  });
}

export function editableToSchedule(days: EditableDay[]): WeeklySchedule {
  return {
    days: days
      .filter(item => item.enabled)
      .map(item => ({
        day: item.day,
        windows: item.windows.map(window => ({ start: `${window.start}:00`, end: `${window.end}:00` })),
      })),
  };
}

export function validateEditableSchedule(days: EditableDay[]): string | null {
  const enabledWindows = days.flatMap(day => day.enabled ? day.windows : []);
  if (enabledWindows.some(window => parseMinutes(window.start) === null || parseMinutes(window.end) === null)) {
    return "Enter a start and end time for every enabled range.";
  }
  if (findOverlappingWindowIds(days).size > 0) {
    return "Time ranges cannot overlap, including overnight ranges that continue into the next day.";
  }
  return null;
}

export function WeeklyScheduleEditor({ days, onChange }: { days: EditableDay[]; onChange: (days: EditableDay[]) => void }) {
  const overlappingIds = findOverlappingWindowIds(days);

  function updateDay(index: number, values: Partial<EditableDay>) {
    onChange(days.map((day, position) => position === index ? { ...day, ...values } : day));
  }

  function updateWindow(dayIndex: number, windowId: string, values: Partial<EditableWindow>) {
    updateDay(dayIndex, {
      windows: days[dayIndex].windows.map(window => window.id === windowId ? { ...window, ...values } : window),
    });
  }

  function addWindow(dayIndex: number) {
    const previousEnd = days[dayIndex].windows.at(-1)?.end ?? "12:00";
    const startMinutes = parseMinutes(previousEnd) ?? 12 * 60;
    const suggestedEnd = formatMinutes(startMinutes + 60);
    updateDay(dayIndex, {
      enabled: true,
      windows: [
        ...days[dayIndex].windows,
        { id: `${days[dayIndex].day}-${Date.now()}-${days[dayIndex].windows.length}`, start: previousEnd, end: suggestedEnd },
      ],
    });
  }

  function removeWindow(dayIndex: number, windowId: string) {
    const windows = days[dayIndex].windows.filter(window => window.id !== windowId);
    updateDay(dayIndex, { windows, enabled: windows.length > 0 });
  }

  return (
    <div>
      {days.map((item, dayIndex) => (
        <div key={item.day}>
          {dayIndex > 0 && <Separator />}
          <div className="grid gap-3 py-4 sm:grid-cols-[9rem_1fr]">
            <label className="flex h-8 items-center gap-3 text-sm font-medium">
              <Switch
                checked={item.enabled}
                onCheckedChange={checked => updateDay(dayIndex, {
                  enabled: checked,
                  windows: checked && item.windows.length === 0
                    ? [{ id: `${item.day}-${Date.now()}-0`, start: "07:00", end: "22:00" }]
                    : item.windows,
                })}
                aria-label={`Enable ${item.day}`}
              />
              {item.day}
            </label>
            <div className="grid gap-2">
              {item.windows.map((window, windowIndex) => {
                const overlaps = overlappingIds.has(window.id);
                return (
                  <div key={window.id}>
                    <div className="flex items-center gap-2 text-xs text-muted-foreground">
                      <Input
                        className="w-full sm:w-32"
                        aria-label={`${item.day} range ${windowIndex + 1} start`}
                        aria-invalid={overlaps}
                        type="time"
                        value={window.start}
                        disabled={!item.enabled}
                        required={item.enabled}
                        onChange={event => updateWindow(dayIndex, window.id, { start: event.target.value })}
                      />
                      <span>to</span>
                      <Input
                        className="w-full sm:w-32"
                        aria-label={`${item.day} range ${windowIndex + 1} end`}
                        aria-invalid={overlaps}
                        type="time"
                        value={window.end}
                        disabled={!item.enabled}
                        required={item.enabled}
                        onChange={event => updateWindow(dayIndex, window.id, { end: event.target.value })}
                      />
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Remove ${item.day} range ${windowIndex + 1}`}
                        disabled={!item.enabled}
                        onClick={() => removeWindow(dayIndex, window.id)}
                      >
                        <Trash2 />
                      </Button>
                    </div>
                    {overlaps && <p className="mt-1 text-xs text-destructive">This range overlaps another allowed range.</p>}
                  </div>
                );
              })}
              <Button type="button" variant="outline" size="sm" className="w-fit" onClick={() => addWindow(dayIndex)}>
                <Plus data-icon="inline-start" /> Add time range
              </Button>
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

function findOverlappingWindowIds(days: EditableDay[]): Set<string> {
  const weekMinutes = 7 * 24 * 60;
  const intervals: { id: string; start: number; end: number }[] = [];

  days.forEach((day, dayIndex) => {
    if (!day.enabled) return;
    day.windows.forEach(window => {
      const startTime = parseMinutes(window.start);
      const endTime = parseMinutes(window.end);
      if (startTime === null || endTime === null) return;
      const start = dayIndex * 24 * 60 + startTime;
      const end = startTime === endTime
        ? start + 24 * 60
        : dayIndex * 24 * 60 + endTime + (startTime > endTime ? 24 * 60 : 0);
      if (end <= weekMinutes) {
        intervals.push({ id: window.id, start, end });
      } else {
        intervals.push({ id: window.id, start, end: weekMinutes });
        intervals.push({ id: window.id, start: 0, end: end - weekMinutes });
      }
    });
  });

  const overlapping = new Set<string>();
  for (let left = 0; left < intervals.length; left++) {
    for (let right = left + 1; right < intervals.length; right++) {
      if (intervals[left].id === intervals[right].id) continue;
      if (intervals[left].start < intervals[right].end && intervals[right].start < intervals[left].end) {
        overlapping.add(intervals[left].id);
        overlapping.add(intervals[right].id);
      }
    }
  }
  return overlapping;
}

function parseMinutes(value: string): number | null {
  const match = /^(\d{2}):(\d{2})$/.exec(value);
  if (!match) return null;
  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  if (hours > 23 || minutes > 59) return null;
  return hours * 60 + minutes;
}

function formatMinutes(value: number): string {
  const normalized = value % (24 * 60);
  return `${String(Math.floor(normalized / 60)).padStart(2, "0")}:${String(normalized % 60).padStart(2, "0")}`;
}
