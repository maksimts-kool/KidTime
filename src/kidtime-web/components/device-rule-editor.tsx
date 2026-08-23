"use client";

import { FormEvent, useState } from "react";
import { Check } from "lucide-react";
import { useRouter } from "next/navigation";
import type { DeviceRule, WindowsUserAccount } from "@/lib/types";
import { editableToSchedule, scheduleToEditable, validateEditableSchedule, WeeklyScheduleEditor } from "./weekly-schedule-editor";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field, FieldContent, FieldDescription, FieldGroup, FieldLabel, FieldTitle } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";

export function DeviceRuleEditor({ deviceId, rule, accounts }: { deviceId: string; rule: DeviceRule; accounts: WindowsUserAccount[] }) {
  const router = useRouter();
  const [days, setDays] = useState(() => scheduleToEditable(rule.schedule));
  const [limitEnabled, setLimitEnabled] = useState(rule.dailyLimitSeconds != null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaved(false); setError("");
    const scheduleError = validateEditableSchedule(days);
    if (scheduleError) {
      setError(scheduleError);
      return;
    }
    setBusy(true);
    const data = new FormData(event.currentTarget);
    const hours = Number(data.get("limitHours"));
    const minutes = Number(data.get("limitMinutes"));
    try {
      const response = await fetch(`/api/backend/devices/${deviceId}/rules`, {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          controlledUserSid: data.get("controlledUserSid") || null,
          dailyLimitSeconds: limitEnabled ? hours * 3600 + minutes * 60 : null,
          idleThresholdSeconds: Number(data.get("idleMinutes")) * 60,
          schedule: editableToSchedule(days),
        }),
      });
      if (!response.ok) {
        setError(response.status === 400 ? "Check the limit, schedule, and idle values, then try again." : "The PC rule could not be saved.");
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
    <form onSubmit={save} className="grid gap-4">
      <Card>
        <CardHeader><CardTitle>Controlled Windows account</CardTitle><CardDescription>KidTime counts usage and enforces limits only while this Windows profile is signed in.</CardDescription></CardHeader>
        <CardContent>
          <Field className="max-w-xl">
            <FieldLabel htmlFor="controlledUserSid">Standard user profile</FieldLabel>
            <select
              id="controlledUserSid"
              name="controlledUserSid"
              defaultValue={rule.controlledUserSid ?? ""}
              className="h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              <option value="">No account selected — protection inactive</option>
              {rule.controlledUserSid && !accounts.some(account => account.sid === rule.controlledUserSid) && (
                <option value={rule.controlledUserSid}>{rule.controlledUserName ?? "Previously selected account"} — not currently reported</option>
              )}
              {accounts.map(account => (
                <option key={account.sid} value={account.sid} disabled={!account.isEnabled || account.isAdministrator}>
                  {account.displayName} ({account.accountName}){account.isAdministrator ? " — administrator" : account.isEnabled ? "" : " — disabled"}
                </option>
              ))}
            </select>
            <FieldDescription>Create the child as a Standard User in Windows, wait up to one minute for it to appear here, then select it. Administrator and other profiles remain unaffected.</FieldDescription>
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Daily PC limit</CardTitle><CardDescription>Only active, non-idle time counts toward this limit.</CardDescription></CardHeader>
        <CardContent>
          <FieldGroup>
            <Field orientation="horizontal">
              <FieldContent><FieldTitle>Use a daily limit</FieldTitle><FieldDescription>Windows signs the user out when active time is exhausted.</FieldDescription></FieldContent>
              <Switch checked={limitEnabled} onCheckedChange={setLimitEnabled} aria-label="Use a daily PC limit" />
            </Field>
            <div className="grid max-w-sm grid-cols-2 gap-3">
              <Field><FieldLabel htmlFor="limitHours">Hours</FieldLabel><Input id="limitHours" name="limitHours" type="number" min="0" max="24" disabled={!limitEnabled} defaultValue={Math.floor((rule.dailyLimitSeconds ?? 18000) / 3600)} /></Field>
              <Field><FieldLabel htmlFor="limitMinutes">Minutes</FieldLabel><Input id="limitMinutes" name="limitMinutes" type="number" min="0" max="59" disabled={!limitEnabled} defaultValue={Math.floor(((rule.dailyLimitSeconds ?? 18000) % 3600) / 60)} /></Field>
            </div>
          </FieldGroup>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Weekly schedule</CardTitle><CardDescription>Add as many non-overlapping access ranges as needed. Overnight windows are supported.</CardDescription></CardHeader>
        <CardContent><WeeklyScheduleEditor days={days} onChange={setDays} /><p className="mt-3 text-xs text-muted-foreground">Leave every day disabled to allow the PC at all times.</p></CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Idle detection</CardTitle><CardDescription>Usage pauses after this long without keyboard or mouse input.</CardDescription></CardHeader>
        <CardContent><Field className="max-w-48"><FieldLabel htmlFor="idleMinutes">Minutes until idle</FieldLabel><Input id="idleMinutes" name="idleMinutes" type="number" min="1" max="60" defaultValue={Math.round(rule.idleThresholdSeconds / 60)} /></Field></CardContent>
      </Card>

      <div className="flex flex-col-reverse items-stretch gap-3 sm:flex-row sm:items-center sm:justify-end">
        <span aria-live="polite">
          {saved && <span className="flex items-center gap-1.5 text-sm text-primary"><Check className="size-4" />Changes saved and queued.</span>}
          {error && <span className="text-sm text-destructive">{error}</span>}
        </span>
        <Button type="submit" size="lg" disabled={busy}>{busy ? "Saving…" : "Save changes"}</Button>
      </div>
    </form>
  );
}
