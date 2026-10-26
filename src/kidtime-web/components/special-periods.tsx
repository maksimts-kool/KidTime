"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { CalendarRange, CalendarX2, Plus, Trash2 } from "lucide-react";
import { appPath } from "@/lib/paths";
import { errorMessage, unreachableMessage } from "@/lib/http";
import { formatDuration } from "@/lib/format";
import type { SpecialPeriod, WeeklySchedule } from "@/lib/types";
import { editableToSchedule, scheduleToEditable, validateEditableSchedule, WeeklyScheduleEditor } from "./weekly-schedule-editor";
import { Badge } from "@/components/ui/badge";
import { Dialog, DialogBody, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogMedia,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Field, FieldDescription, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Separator } from "@/components/ui/separator";
import { SwitchField } from "@/components/ui/switch";

import { WeekView, weekdays } from "./week-view";

/** "2026-12-24" as "24 Dec 2026", in the panel's own locale so it never depends on the browser's. */
function formatDate(date: string) {
  return new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" })
    .format(new Date(`${date}T00:00:00Z`));
}

function rangesOn(schedule: WeeklySchedule, day: string) {
  return schedule.days.find(item => item.day === day)?.windows
    .map(window => `${window.start.slice(0, 5)}–${window.end.slice(0, 5)}`).join(", ") ?? null;
}

function sameEveryDay(schedule: WeeklySchedule) {
  const first = rangesOn(schedule, weekdays[0]);
  return first !== null && weekdays.every(day => rangesOn(schedule, day) === first);
}

/**
 * A period's schedule in one line: "Every day 12:00–15:00, 18:00–22:00" when the days agree,
 * which is what a holiday usually looks like. When they differ the week is drawn beneath instead.
 */
function describeWindows(schedule: WeeklySchedule) {
  if (schedule.days.length === 0) return "Any time";
  return sameEveryDay(schedule) ? `Every day ${rangesOn(schedule, weekdays[0])}` : "Own hours each day";
}

/**
 * Holidays, trips and other dates that run on rules of their own, for the PC or for one
 * application. A period replaces the weekly schedule and the daily limit on its dates, and the PC
 * carries upcoming ones offline, so it switches over at its own midnight with nothing sent then.
 *
 * The tab only lists them. Adding one happens in a popup and removing one asks first; both save
 * at once.
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
  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">
          Holidays, trips and other dates when {scope} follows a different schedule and daily limit. Afterwards the weekly rules come back on their own.
        </p>
        <AddPeriodDialog
          deviceId={deviceId}
          deviceApplicationId={deviceApplicationId}
          schedule={schedule}
          dailyLimitSeconds={dailyLimitSeconds}
        />
      </div>
      {periods.length === 0 && (
        <p className="rounded-lg border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">No special periods coming up.</p>
      )}
      {periods.map((period, index) => (
        <div key={period.id}>
          {index > 0 && <Separator className="mb-4" />}
          <div className="flex items-start justify-between gap-3">
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
            <RemovePeriodDialog period={period} />
          </div>
          {/* A holiday whose days differ is easier to read as the week than as a sentence. */}
          {period.schedule.days.length > 0 && !sameEveryDay(period.schedule) && (
            <div className="mt-3 sm:pl-12"><WeekView schedule={period.schedule} /></div>
          )}
        </div>
      ))}
    </div>
  );
}

function RemovePeriodDialog({ period }: { period: SpecialPeriod }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function remove() {
    setBusy(true);
    setError("");
    try {
      const response = await fetch(appPath(`/api/backend/special-periods/${period.id}`), { method: "DELETE" });
      // Already gone is what was asked for; the refresh takes the row and this dialog away.
      if (!response.ok && response.status !== 404) {
        setError(await errorMessage(response, "The period could not be removed."));
        return;
      }
      router.refresh();
    } catch {
      setError(unreachableMessage);
    } finally {
      setBusy(false);
    }
  }

  return (
    <AlertDialog onOpenChange={() => setError("")}>
      <AlertDialogTrigger render={<Button type="button" variant="ghost" size="icon-sm" aria-label={`Remove ${period.name}`} />}>
        <Trash2 />
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogMedia><CalendarX2 /></AlertDialogMedia>
          <AlertDialogTitle>Remove {period.name}?</AlertDialogTitle>
          <AlertDialogDescription>
            {period.isActive
              ? "It is in force now, so the weekly schedule and daily limit apply again from the next sync."
              : "Those dates will follow the weekly schedule and daily limit."}
          </AlertDialogDescription>
        </AlertDialogHeader>
        {error && <p className="text-sm text-destructive">{error}</p>}
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" disabled={busy} onClick={remove}>{busy ? "Removing…" : "Remove period"}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

function AddPeriodDialog({
  deviceId,
  deviceApplicationId,
  schedule,
  dailyLimitSeconds,
}: {
  deviceId: string;
  deviceApplicationId: string | null;
  schedule: WeeklySchedule;
  dailyLimitSeconds: number | null;
}) {
  const router = useRouter();
  const defaultLimit = dailyLimitSeconds ?? 10800;
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [startDate, setStartDate] = useState("");
  const [endDate, setEndDate] = useState("");
  const [days, setDays] = useState(() => scheduleToEditable({ days: schedule.days }));
  const [limitEnabled, setLimitEnabled] = useState(dailyLimitSeconds != null);
  const [hours, setHours] = useState(String(Math.floor(defaultLimit / 3600)));
  const [minutes, setMinutes] = useState(String(Math.floor((defaultLimit % 3600) / 60)));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  /** Every opening starts from the weekly rules as they are now, not from a draft abandoned earlier. */
  function reset(next: boolean) {
    setOpen(next);
    if (!next) return;
    setName("");
    setStartDate("");
    setEndDate("");
    setDays(scheduleToEditable({ days: schedule.days }));
    setLimitEnabled(dailyLimitSeconds != null);
    setHours(String(Math.floor(defaultLimit / 3600)));
    setMinutes(String(Math.floor((defaultLimit % 3600) / 60)));
    setError("");
  }

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    if (!name.trim()) {
      setError("Give the period a name.");
      return;
    }
    if (!startDate || !endDate) {
      setError("Choose the first and last day of the period.");
      return;
    }
    const scheduleError = validateEditableSchedule(days);
    if (scheduleError) {
      setError(scheduleError);
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
          name: name.trim(),
          startDate,
          endDate,
          schedule: editableToSchedule(days),
          dailyLimitSeconds: limitEnabled ? Number(hours) * 3600 + Number(minutes) * 60 : null,
        }),
      });
      if (!response.ok) {
        setError(await errorMessage(response, "The period could not be saved."));
        return;
      }
      setOpen(false);
      router.refresh();
    } catch {
      setError(unreachableMessage);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={reset}>
      <DialogTrigger render={<Button type="button" />}><Plus data-icon="inline-start" />Add special period</DialogTrigger>
      <DialogContent size="lg">
        <form onSubmit={create} className="flex min-h-0 flex-col">
          <DialogHeader>
            <DialogTitle>Add a special period</DialogTitle>
            <DialogDescription>On these dates the schedule and daily limit below replace the weekly ones.</DialogDescription>
          </DialogHeader>
          <DialogBody>
            <FieldGroup>
              <Field className="max-w-sm">
                <FieldLabel htmlFor="periodName">Name</FieldLabel>
                <Input id="periodName" maxLength={100} placeholder="Winter holidays" value={name} onChange={event => setName(event.target.value)} />
              </Field>
              <div className="grid max-w-sm grid-cols-2 gap-3">
                <Field><FieldLabel htmlFor="periodStart">First day</FieldLabel><Input id="periodStart" type="date" value={startDate} onChange={event => setStartDate(event.target.value)} /></Field>
                <Field><FieldLabel htmlFor="periodEnd">Last day</FieldLabel><Input id="periodEnd" type="date" min={startDate || undefined} value={endDate} onChange={event => setEndDate(event.target.value)} /></Field>
              </div>
            </FieldGroup>
            <div>
              <p className="text-sm font-medium">Schedule during the period</p>
              <p className="text-xs text-muted-foreground">Starts from the weekly schedule. Set one day and use Copy to all days for the same hours every day; leave every day off to allow any time.</p>
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
                <Field><FieldLabel htmlFor="periodHours">Hours</FieldLabel><Input id="periodHours" type="number" min="0" max="24" disabled={!limitEnabled} value={hours} onChange={event => setHours(event.target.value)} /></Field>
                <Field><FieldLabel htmlFor="periodMinutes">Minutes</FieldLabel><Input id="periodMinutes" type="number" min="0" max="59" disabled={!limitEnabled} value={minutes} onChange={event => setMinutes(event.target.value)} /></Field>
              </div>
              <FieldDescription>Extra time you give or approve on a day in the period is added on top, as usual.</FieldDescription>
            </FieldGroup>
            {error && <p className="text-sm text-destructive">{error}</p>}
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
            <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Add period"}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
