"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { CalendarClock, Eraser, Hourglass, Pencil, Plus, Trash2 } from "lucide-react";
import { appPath } from "@/lib/paths";
import { errorMessage, unreachableMessage } from "@/lib/http";
import { formatDuration } from "@/lib/format";
import type { SpecialPeriod, WeeklySchedule } from "@/lib/types";
import { editableToSchedule, scheduleToEditable, validateEditableSchedule, type EditableDay } from "./weekly-schedule-editor";
import { SpecialPeriods } from "./special-periods";
import { WeekView, weekdays, windowsOf } from "./week-view";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogBody, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Tabs, TabsList, TabsPanel, TabsTab } from "@/components/ui/tabs";
import { TimeInput } from "@/components/ui/time-input";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { SwitchField } from "@/components/ui/switch";
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

const presets = [
  { label: "Every day", days: weekdays },
  { label: "Weekdays", days: weekdays.slice(0, 5) },
  { label: "Weekend", days: weekdays.slice(5) },
];

type Range = { id: string; start: string; end: string };

/**
 * When the PC or one application may be used, and for how long, as a card that only shows it. The
 * weekly tab carries the daily limit and draws the week - the two rules that hold every week, read
 * together the way a parent thinks of them ("two hours, and only in the evening") - and the second
 * tab lists the special periods, which replace both. Every change - the limit, adding a range,
 * editing a day, clearing the week, adding or removing a period - happens in a popup and is saved
 * the moment the popup is confirmed, through its own endpoint, so it never waits on the rule form
 * below and never carries that form's unsaved edits with it.
 */
export function ScheduleCard({
  scheduleEndpoint,
  limitEndpoint,
  schedule,
  periods,
  deviceId,
  deviceApplicationId = null,
  dailyLimitSeconds,
  scope,
  description,
}: {
  /** `/api/backend/...` path the week is PUT to. */
  scheduleEndpoint: string;
  /** `/api/backend/...` path the daily limit is PUT to. */
  limitEndpoint: string;
  schedule: WeeklySchedule;
  periods: SpecialPeriod[];
  deviceId: string;
  deviceApplicationId?: string | null;
  dailyLimitSeconds: number | null;
  /** "the PC" or the application's name, for the sentences. */
  scope: string;
  description: string;
}) {
  const router = useRouter();
  const week: WeeklySchedule = { days: schedule.days };
  const configured = week.days.length > 0;
  const activePeriod = periods.find(period => period.isActive);

  /** Saves a whole week as a popup left it; returns why not, or null. */
  async function saveWeek(days: EditableDay[]): Promise<string | null> {
    const problem = validateEditableSchedule(days);
    if (problem) return problem;
    try {
      const response = await fetch(appPath(scheduleEndpoint), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(editableToSchedule(days)),
      });
      if (!response.ok) {
        return await errorMessage(response, "The schedule could not be saved.");
      }
      router.refresh();
      return null;
    } catch {
      return unreachableMessage;
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Schedule and daily limit</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent>
        <Tabs defaultValue="weekly">
          <TabsList>
            <TabsTab value="weekly">Every week</TabsTab>
            <TabsTab value="periods">
              Special periods
              {periods.length > 0 && <span className="rounded-full bg-primary/10 px-1.5 text-xs text-primary tabular-nums">{periods.length}</span>}
            </TabsTab>
          </TabsList>

          <TabsPanel value="weekly" className="grid gap-4">
            <div className="flex flex-col gap-3 rounded-lg bg-muted/50 p-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex items-start gap-3">
                <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary"><Hourglass className="size-4" /></span>
                <div className="min-w-0">
                  <p className="text-xs text-muted-foreground">Daily limit</p>
                  <p className="text-sm font-medium">{dailyLimitSeconds == null ? "No daily limit" : `${formatDuration(dailyLimitSeconds)} a day`}</p>
                  <p className="text-xs text-muted-foreground">
                    {activePeriod
                      ? `${activePeriod.name} is in force, so its own limit applies today.`
                      : "Only active time counts; time spent idle does not."}
                  </p>
                </div>
              </div>
              <LimitDialog endpoint={limitEndpoint} dailyLimitSeconds={dailyLimitSeconds} scope={scope} />
            </div>

            <div className="flex flex-wrap items-center justify-between gap-2">
              <p className="text-sm text-muted-foreground">
                {configured
                  ? `${scope[0].toUpperCase()}${scope.slice(1)} can be used only inside these ranges. Overnight ranges run past midnight.`
                  : `No weekly schedule: ${scope} can be used at any time, within the daily limit.`}
              </p>
              <div className="flex flex-wrap gap-2">
                <AddRangeDialog week={week} onSave={saveWeek} />
                {configured && <ClearWeekDialog scope={scope} onClear={() => saveWeek([])} />}
              </div>
            </div>
            {configured && (
              <WeekView schedule={week} dayAction={day => <EditDayDialog day={day} week={week} onSave={saveWeek} />} />
            )}
          </TabsPanel>

          <TabsPanel value="periods">
            <SpecialPeriods
              deviceId={deviceId}
              deviceApplicationId={deviceApplicationId}
              periods={periods}
              schedule={week}
              dailyLimitSeconds={dailyLimitSeconds}
              scope={scope}
            />
          </TabsPanel>
        </Tabs>
      </CardContent>
    </Card>
  );
}

/** One or more days, one range: "Every day, 12:00 to 15:00". */
function AddRangeDialog({ week, onSave }: { week: WeeklySchedule; onSave: (days: EditableDay[]) => Promise<string | null> }) {
  const [open, setOpen] = useState(false);
  const [days, setDays] = useState<string[]>([]);
  const [start, setStart] = useState("12:00");
  const [end, setEnd] = useState("15:00");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  function reset(next: boolean) {
    setOpen(next);
    if (next) {
      setDays([]);
      setStart("12:00");
      setEnd("15:00");
      setError("");
    }
  }

  function toggle(day: string) {
    setDays(current => current.includes(day) ? current.filter(item => item !== day) : [...current, day]);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (days.length === 0) {
      setError("Choose at least one day.");
      return;
    }
    setBusy(true);
    const stamp = Date.now();
    const editable = scheduleToEditable(week).map(item => {
      if (!days.includes(item.day)) return item;
      const range = { id: `${item.day}-${stamp}`, start, end };
      // A day that had nothing carries a placeholder range for the inline editor; it goes.
      return { ...item, enabled: true, windows: item.enabled ? [...item.windows, range] : [range] };
    });
    const problem = await onSave(editable);
    setBusy(false);
    if (problem) setError(problem);
    else setOpen(false);
  }

  return (
    <Dialog open={open} onOpenChange={reset}>
      <DialogTrigger render={<Button type="button" />}><Plus data-icon="inline-start" />Add time range</DialogTrigger>
      <DialogContent>
        <form onSubmit={submit} className="flex min-h-0 flex-col">
          <DialogHeader>
            <DialogTitle>Add a time range</DialogTitle>
            <DialogDescription>Allowed on every day you pick. A range that ends earlier than it starts runs past midnight.</DialogDescription>
          </DialogHeader>
          <DialogBody>
            <div className="grid gap-2">
              <span className="text-xs text-muted-foreground">Days</span>
              <div className="flex flex-wrap gap-1.5">
                {weekdays.map(day => (
                  <Button key={day} type="button" size="sm" variant={days.includes(day) ? "default" : "outline"} aria-pressed={days.includes(day)} onClick={() => toggle(day)}>
                    {day.slice(0, 3)}
                  </Button>
                ))}
              </div>
              <div className="flex flex-wrap gap-1.5">
                {presets.map(preset => (
                  <Button key={preset.label} type="button" size="sm" variant="ghost" className="text-muted-foreground" onClick={() => setDays(preset.days)}>
                    {preset.label}
                  </Button>
                ))}
              </div>
            </div>
            <div className="flex items-end gap-2">
              <label className="grid gap-1.5 text-xs text-muted-foreground">From<TimeInput className="w-24 tabular-nums" value={start} onValueChange={setStart} aria-label="From" /></label>
              <span className="pb-1.5 text-sm text-muted-foreground">to</span>
              <label className="grid gap-1.5 text-xs text-muted-foreground">To<TimeInput className="w-24 tabular-nums" value={end} onValueChange={setEnd} aria-label="To" /></label>
            </div>
            {error && <p className="text-sm text-destructive">{error}</p>}
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
            <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Add range"}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Every range of one day, to change, remove, or add to. No ranges left closes the day. */
function EditDayDialog({ day, week, onSave }: { day: string; week: WeeklySchedule; onSave: (days: EditableDay[]) => Promise<string | null> }) {
  const [open, setOpen] = useState(false);
  const [ranges, setRanges] = useState<Range[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  function reset(next: boolean) {
    setOpen(next);
    if (next) {
      setRanges(windowsOf(week, day).map((window, index) => {
        const [start, end] = window.split("–");
        return { id: `${day}-${index}`, start, end };
      }));
      setError("");
    }
  }

  function update(id: string, values: Partial<Range>) {
    setRanges(current => current.map(range => range.id === id ? { ...range, ...values } : range));
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    const editable = scheduleToEditable(week).map(item =>
      item.day === day ? { ...item, enabled: ranges.length > 0, windows: ranges } : item);
    // A week with no ranges at all is no schedule - open at any time - which is the opposite of
    // what emptying the last open day looks like it does. That is what Clear is for, said out loud.
    if (!editable.some(item => item.enabled)) {
      setBusy(false);
      setError("This is the last day with any ranges, and a week with none means no schedule at all - allowed at any time. Use Clear if that is what you want.");
      return;
    }
    const problem = await onSave(editable);
    setBusy(false);
    if (problem) setError(problem);
    else setOpen(false);
  }

  return (
    <Dialog open={open} onOpenChange={reset}>
      <DialogTrigger render={<Button type="button" variant="ghost" size="icon-sm" aria-label={`Edit ${day}`} />}><Pencil /></DialogTrigger>
      <DialogContent>
        <form onSubmit={submit} className="flex min-h-0 flex-col">
          <DialogHeader>
            <DialogTitle>{day}</DialogTitle>
            <DialogDescription>Change or remove this day&apos;s ranges. With none left, the day is closed.</DialogDescription>
          </DialogHeader>
          <DialogBody>
            {ranges.length === 0 && <p className="text-sm text-muted-foreground">Closed all day.</p>}
            {ranges.map((range, index) => (
              <div key={range.id} className="flex items-center gap-2 text-sm text-muted-foreground">
                <TimeInput className="w-24 tabular-nums" aria-label={`Range ${index + 1} from`} value={range.start} onValueChange={value => update(range.id, { start: value })} />
                <span>to</span>
                <TimeInput className="w-24 tabular-nums" aria-label={`Range ${index + 1} to`} value={range.end} onValueChange={value => update(range.id, { end: value })} />
                <Button type="button" variant="ghost" size="icon-sm" aria-label={`Remove range ${index + 1}`} onClick={() => setRanges(current => current.filter(item => item.id !== range.id))}>
                  <Trash2 />
                </Button>
              </div>
            ))}
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="w-fit"
              onClick={() => setRanges(current => [...current, { id: `${day}-${Date.now()}`, start: current.at(-1)?.end ?? "12:00", end: "22:00" }])}
            >
              <Plus data-icon="inline-start" />Add range
            </Button>
            {error && <p className="text-sm text-destructive">{error}</p>}
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
            <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save day"}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function ClearWeekDialog({ scope, onClear }: { scope: string; onClear: () => Promise<string | null> }) {
  const [busy, setBusy] = useState(false);
  return (
    <AlertDialog>
      <AlertDialogTrigger render={<Button type="button" variant="outline" />}>
        <Eraser data-icon="inline-start" />Clear
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogMedia><CalendarClock /></AlertDialogMedia>
          <AlertDialogTitle>Remove the weekly schedule?</AlertDialogTitle>
          <AlertDialogDescription>Every range goes and {scope} can be used at any time, within the daily limit. Special periods stay as they are.</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" disabled={busy} onClick={async () => { setBusy(true); await onClear(); setBusy(false); }}>
            {busy ? "Saving…" : "Remove schedule"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

/** The limit on its own: on or off, and how long. Saved through its own endpoint at once. */
function LimitDialog({ endpoint, dailyLimitSeconds, scope }: { endpoint: string; dailyLimitSeconds: number | null; scope: string }) {
  const router = useRouter();
  const fallback = dailyLimitSeconds ?? 7200;
  const [open, setOpen] = useState(false);
  const [enabled, setEnabled] = useState(dailyLimitSeconds != null);
  const [hours, setHours] = useState(String(Math.floor(fallback / 3600)));
  const [minutes, setMinutes] = useState(String(Math.floor((fallback % 3600) / 60)));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  function reset(next: boolean) {
    setOpen(next);
    if (!next) return;
    setEnabled(dailyLimitSeconds != null);
    setHours(String(Math.floor(fallback / 3600)));
    setMinutes(String(Math.floor((fallback % 3600) / 60)));
    setError("");
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const seconds = Number(hours) * 3600 + Number(minutes) * 60;
    if (enabled && !(seconds >= 60 && seconds <= 86_400)) {
      setError("Set a limit between 1 minute and 24 hours.");
      return;
    }
    setBusy(true);
    setError("");
    try {
      const response = await fetch(appPath(endpoint), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ dailyLimitSeconds: enabled ? seconds : null }),
      });
      if (!response.ok) {
        setError(await errorMessage(response, "The limit could not be saved."));
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
      <DialogTrigger render={<Button type="button" variant="outline" className="shrink-0" />}><Pencil data-icon="inline-start" />Change</DialogTrigger>
      <DialogContent>
        <form onSubmit={submit} className="flex min-h-0 flex-col">
          <DialogHeader>
            <DialogTitle>Daily limit</DialogTitle>
            <DialogDescription>How much active time {scope} gets each day. When it runs out, {scope === "the PC" ? "Windows signs the child out" : "it closes"} after a warning.</DialogDescription>
          </DialogHeader>
          <DialogBody>
            <FieldGroup>
              <SwitchField
                title="Use a daily limit"
                description="Off means no limit at all - only the schedule applies."
                checked={enabled}
                onCheckedChange={setEnabled}
              />
              <div className="grid max-w-xs grid-cols-2 gap-3">
                <Field><FieldLabel htmlFor="limitDialogHours">Hours</FieldLabel><Input id="limitDialogHours" type="number" min="0" max="24" disabled={!enabled} value={hours} onChange={event => setHours(event.target.value)} /></Field>
                <Field><FieldLabel htmlFor="limitDialogMinutes">Minutes</FieldLabel><Input id="limitDialogMinutes" type="number" min="0" max="59" disabled={!enabled} value={minutes} onChange={event => setMinutes(event.target.value)} /></Field>
              </div>
            </FieldGroup>
            {error && <p className="text-sm text-destructive">{error}</p>}
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
            <Button type="submit" disabled={busy}>{busy ? "Saving…" : "Save limit"}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
