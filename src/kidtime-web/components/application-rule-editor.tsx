"use client";

import { FormEvent, useState } from "react";
import { Check } from "lucide-react";
import { useRouter } from "next/navigation";
import type { WeeklySchedule } from "@/lib/types";
import { editableToSchedule, scheduleToEditable, validateEditableSchedule, WeeklyScheduleEditor } from "./weekly-schedule-editor";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field, FieldContent, FieldDescription, FieldGroup, FieldLabel, FieldTitle } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";

export function ApplicationRuleEditor({ applicationId, blocked, dailyLimitSeconds, schedule }: { applicationId: string; blocked: boolean; dailyLimitSeconds: number | null; schedule: WeeklySchedule }) {
  const router = useRouter();
  const [days, setDays] = useState(() => scheduleToEditable(schedule));
  const [isBlocked, setIsBlocked] = useState(blocked);
  const [limitEnabled, setLimitEnabled] = useState(dailyLimitSeconds != null);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState("");
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setSaved(false); setError("");
    const scheduleError = validateEditableSchedule(days);
    if (scheduleError) {
      setError(scheduleError);
      return;
    }
    setBusy(true);
    const data = new FormData(event.currentTarget);
    try {
      const result = await fetch(`/api/backend/applications/${applicationId}/rules`, {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ manuallyBlocked: isBlocked, dailyLimitSeconds: limitEnabled ? Number(data.get("hours")) * 3600 + Number(data.get("minutes")) * 60 : null, schedule: editableToSchedule(days) }),
      });
      if (!result.ok) {
        setError(result.status === 400 ? "Check the limit and schedule values, then try again." : "The application rule could not be saved.");
        return;
      }
      setSaved(true);
      router.refresh();
    } catch {
      setError("Could not reach the KidTime server. Try again in a moment.");
    } finally {
      setBusy(false);
    }
  }
  return (
    <form className="grid gap-4" onSubmit={save}>
      <Card>
        <CardHeader><CardTitle>Application access</CardTitle><CardDescription>Manual blocks also work from cached rules while the PC is offline.</CardDescription></CardHeader>
        <CardContent>
          <Field orientation="horizontal">
            <FieldContent><FieldTitle>Block this application</FieldTitle><FieldDescription>Matching processes are closed and Windows explains why.</FieldDescription></FieldContent>
            <Switch checked={isBlocked} onCheckedChange={setIsBlocked} aria-label="Block this application" />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Daily application limit</CardTitle><CardDescription>Only foreground active time is counted.</CardDescription></CardHeader>
        <CardContent>
          <FieldGroup>
            <Field orientation="horizontal">
              <FieldContent><FieldTitle>Use a daily limit</FieldTitle><FieldDescription>Notifications are shown when the app opens and at 15, 5, and 2 minutes remaining.</FieldDescription></FieldContent>
              <Switch checked={limitEnabled} onCheckedChange={setLimitEnabled} aria-label="Use a daily application limit" />
            </Field>
            <div className="grid max-w-sm grid-cols-2 gap-3">
              <Field><FieldLabel htmlFor="hours">Hours</FieldLabel><Input id="hours" name="hours" type="number" min="0" max="24" disabled={!limitEnabled} defaultValue={Math.floor((dailyLimitSeconds ?? 7200) / 3600)} /></Field>
              <Field><FieldLabel htmlFor="minutes">Minutes</FieldLabel><Input id="minutes" name="minutes" type="number" min="0" max="59" disabled={!limitEnabled} defaultValue={Math.floor(((dailyLimitSeconds ?? 7200) % 3600) / 60)} /></Field>
            </div>
          </FieldGroup>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Weekly schedule</CardTitle><CardDescription>Add multiple non-overlapping access ranges. The application closes when its current allowed range ends.</CardDescription></CardHeader>
        <CardContent><WeeklyScheduleEditor days={days} onChange={setDays} /><p className="mt-3 text-xs text-muted-foreground">Leave every day disabled to allow the application at all times.</p></CardContent>
      </Card>

      <div className="flex flex-col-reverse items-stretch gap-3 sm:flex-row sm:items-center sm:justify-end">
        <span aria-live="polite">
          {saved && <span className="flex items-center gap-1.5 text-sm text-primary"><Check className="size-4" />Application rule saved.</span>}
          {error && <span className="text-sm text-destructive">{error}</span>}
        </span>
        <Button type="submit" size="lg" disabled={busy}>{busy ? "Saving…" : "Save application rule"}</Button>
      </div>
    </form>
  );
}
