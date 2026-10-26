"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { CalendarRange, Plus, Trash2 } from "lucide-react";
import { appPath } from "@/lib/paths";
import { formatDuration } from "@/lib/format";
import type { SpecialPeriod, WeeklySchedule } from "@/lib/types";
import { editableToSchedule, scheduleToEditable, validateEditableSchedule, WeeklyScheduleEditor } from "./weekly-schedule-editor";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Separator } from "@/components/ui/separator";
import { SwitchField } from "@/components/ui/switch";

const weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

/** "2026-12-24" as "24 Dec 2026", in the panel's own locale so it never depends on the browser's. */
function formatDate(date: string) {
  return new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" })
    .format(new Date(`${date}T00:00:00Z`));
}

/**
 * A period's windows in one line: "Every day 12:00–15:00, 18:00–22:00" when the days agree, which
 * is what a holiday usually looks like, and day by day when they do not.
 */
function describeWindows(schedule: WeeklySchedule) {
  if (schedule.days.length === 0) return "Any time";
  const ranges = (day: string) =>
    schedule.days.find(item => item.day === day)?.windows
      .map(window => `${window.start.slice(0, 5)}–${window.end.slice(0, 5)}`).join(", ") ?? null;
  const all = weekdays.map(ranges);
  if (all.every(value => value === all[0]) && all[0]) return `Every day ${all[0]}`;
  return weekdays
    .map((day, index) => `${day.slice(0, 3)} ${all[index] ?? "closed"}`)
    .join(" · ");
}

/**
 * Holidays, trips and other dates that run on rules of their own, for the PC or for one
 * application. A period replaces the weekly schedule and the daily limit on its dates, and the PC
 * carries upcoming ones offline, so it switches over at its own midnight with nothing sent then.
 */
export function SpecialPeriods({
  deviceId,
  deviceApplicationId = null,
  periods,
  schedule,
  dailyLimitSeconds,
  scope,
}: {
  deviceId: string;
  deviceApplicationId?: string | null;
  periods: SpecialPeriod[];
  /** The weekly schedule a new period starts from, so only the difference has to be entered. */
  schedule: WeeklySchedule;
  dailyLimitSeconds: number | null;
  /** "the PC" or the application's name, for the sentences. */
  scope: string;
}) {
  const router = useRouter();
  const [adding, setAdding] = useState(false);
  const [days, setDays] = useState(() => scheduleToEditable({ days: schedule.days }));
  const [limitEnabled, setLimitEnabled] = useState(dailyLimitSeconds != null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    const scheduleError = validateEditableSchedule(days);
    if (scheduleError) {
      setError(scheduleError);
      return;
    }
    const data = new FormData(event.currentTarget);
    const startDate = String(data.get("startDate") ?? "");
    const endDate = String(data.get("endDate") ?? "");
    if (!startDate || !endDate) {
      setError("Choose the first and last day of the period.");
      return;
    }
    setBusy(true);
    try {
      const response = await fetch(appPath("/api/backend/special-periods"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          deviceId,
          deviceApplicationId,
          name: String(data.get("name") ?? "").trim(),
          startDate,
          endDate,
          schedule: editableToSchedule(days),
          dailyLimitSeconds: limitEnabled
            ? Number(data.get("periodHours")) * 3600 + Number(data.get("periodMinutes")) * 60
            : null,
        }),
      });
      if (!response.ok) {
        const body = (await response.text()).trim().replace(/^"|"$/g, "");
        setError(response.status === 400 && body ? body : "The period could not be saved.");
        return;
      }
      setAdding(false);
      router.refresh();
    } catch {
      setError("Could not reach the KidTime server. Try again in a moment.");
    } finally {
      setBusy(false);
    }
  }

  async function remove(periodId: string) {
    setBusy(true);
    try {
      await fetch(appPath(`/api/backend/special-periods/${periodId}`), { method: "DELETE" });
      router.refresh();
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Special periods</CardTitle>
        <CardDescription>
          Holidays, trips and other dates when {scope} follows a different schedule and daily limit. On those dates they replace the weekly rules; afterwards everything goes back on its own.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {periods.length === 0 && !adding && (
          <p className="text-sm text-muted-foreground">No special periods coming up.</p>
        )}
        {periods.map((period, index) => (
          <div key={period.id}>
            {index > 0 && <Separator className="mb-4" />}
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="flex min-w-0 items-start gap-3">
                <span className="mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground"><CalendarRange className="size-4" /></span>
                <div className="min-w-0">
                  <p className="flex flex-wrap items-center gap-2 font-medium">
                    {period.name}
                    {period.isActive && <Badge variant="secondary">In force now</Badge>}
                  </p>
                  <p className="text-sm text-muted-foreground">
                    {period.startDate === period.endDate ? formatDate(period.startDate) : `${formatDate(period.startDate)} – ${formatDate(period.endDate)}`}
                  </p>
                  <p className="mt-1 text-sm">
                    {describeWindows(period.schedule)} · {period.dailyLimitSeconds == null ? "no daily limit" : `${formatDuration(period.dailyLimitSeconds)} daily`}
                  </p>
                </div>
              </div>
              <Button type="button" variant="ghost" size="sm" disabled={busy} onClick={() => remove(period.id)}>
                <Trash2 data-icon="inline-start" />Remove
              </Button>
            </div>
          </div>
        ))}

        {adding ? (
          <form onSubmit={create} className="grid gap-4 rounded-lg border p-4">
            <FieldGroup>
              <Field className="max-w-sm">
                <FieldLabel htmlFor="periodName">Name</FieldLabel>
                <Input id="periodName" name="name" required maxLength={100} placeholder="Winter holidays" />
              </Field>
              <div className="grid max-w-sm grid-cols-2 gap-3">
                <Field><FieldLabel htmlFor="startDate">First day</FieldLabel><Input id="startDate" name="startDate" type="date" required /></Field>
                <Field><FieldLabel htmlFor="endDate">Last day</FieldLabel><Input id="endDate" name="endDate" type="date" required /></Field>
              </div>
            </FieldGroup>
            <div>
              <p className="text-sm font-medium">Schedule during the period</p>
              <p className="text-xs text-muted-foreground">Starts from the weekly schedule. Set one day and use Copy to all days for the same hours every day; leave every day disabled to allow any time.</p>
              <WeeklyScheduleEditor days={days} onChange={setDays} />
            </div>
            <FieldGroup>
              <SwitchField
                title="Daily limit during the period"
                description="Replaces the regular daily limit on these dates. Turn it off for no limit at all."
                checked={limitEnabled}
                onCheckedChange={setLimitEnabled}
              />
              <div className="grid max-w-sm grid-cols-2 gap-3">
                <Field><FieldLabel htmlFor="periodHours">Hours</FieldLabel><Input id="periodHours" name="periodHours" type="number" min="0" max="24" disabled={!limitEnabled} defaultValue={Math.floor((dailyLimitSeconds ?? 10800) / 3600)} /></Field>
                <Field><FieldLabel htmlFor="periodMinutes">Minutes</FieldLabel><Input id="periodMinutes" name="periodMinutes" type="number" min="0" max="59" disabled={!limitEnabled} defaultValue={Math.floor(((dailyLimitSeconds ?? 10800) % 3600) / 60)} /></Field>
              </div>
              <FieldDescription>Extra time you give or approve on a day in the period is added on top, as usual.</FieldDescription>
            </FieldGroup>
            <div className="flex flex-col-reverse items-stretch gap-3 sm:flex-row sm:items-center sm:justify-end">
              {error && <span className="text-sm text-destructive">{error}</span>}
              <Button type="button" variant="ghost" onClick={() => { setAdding(false); setError(""); }}>Cancel</Button>
              <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Add period"}</Button>
            </div>
          </form>
        ) : (
          <Button type="button" variant="outline" className="w-fit" onClick={() => setAdding(true)}>
            <Plus data-icon="inline-start" />Add special period
          </Button>
        )}
      </CardContent>
    </Card>
  );
}
