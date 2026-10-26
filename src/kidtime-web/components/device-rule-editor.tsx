"use client";

import { appPath } from "@/lib/paths";
import { unreachableMessage } from "@/lib/http";
import { FormEvent, useState } from "react";
import { Check } from "lucide-react";
import { useRouter } from "next/navigation";
import { agentLanguages, type DeviceRule, type WindowsUserAccount } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { SwitchField } from "@/components/ui/switch";

/**
 * Everything about the PC's rule except its schedule and daily limit, which the schedule card
 * above edits in popups and saves on its own - so this form neither sends them nor overwrites them.
 */
export function DeviceRuleEditor({ deviceId, rule, accounts }: { deviceId: string; rule: DeviceRule; accounts: WindowsUserAccount[] }) {
  const router = useRouter();
  const [openAtSignIn, setOpenAtSignIn] = useState(rule.openWindowAtSignIn);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaved(false); setError("");
    setBusy(true);
    const data = new FormData(event.currentTarget);
    try {
      const response = await fetch(appPath(`/api/backend/devices/${deviceId}/rules`), {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          controlledUserSid: data.get("controlledUserSid") || null,
          language: data.get("language"),
          openWindowAtSignIn: openAtSignIn,
          idleThresholdSeconds: Number(data.get("idleMinutes")) * 60,
        }),
      });
      if (!response.ok) {
        setError(response.status === 400 ? "Check the idle value, then try again." : "The PC rule could not be saved.");
        return;
      }
      setSaved(true);
      router.refresh();
    } catch {
      setError(unreachableMessage);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={save} className="grid gap-4">
      <Card id="account" className="scroll-mt-20">
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
        <CardHeader><CardTitle>Language on the PC</CardTitle><CardDescription>Notifications, the countdown card, and the child&apos;s screen-time window all use this language.</CardDescription></CardHeader>
        <CardContent>
          <Field className="max-w-xs">
            <FieldLabel htmlFor="language">Interface language</FieldLabel>
            <select
              id="language"
              name="language"
              defaultValue={rule.language ?? "English"}
              className="h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              {agentLanguages.map(language => (
                <option key={language.value} value={language.value}>{language.label}</option>
              ))}
            </select>
            <FieldDescription>The panel you are looking at stays in English; this only changes what the child sees.</FieldDescription>
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>The KidTime window on the PC</CardTitle>
          <CardDescription>Where the child reads how much time is left, what the home network blocks, and how to ask you for more minutes.</CardDescription>
        </CardHeader>
        <CardContent>
          <SwitchField
            title="Open it when the child signs in"
            description="The window appears once per sign-in and can be closed straight away. It changes no rule — a tray icon is easy never to notice, and this is how the child learns the window is there."
            checked={openAtSignIn}
            onCheckedChange={setOpenAtSignIn}
          />
        </CardContent>
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
