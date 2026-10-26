"use client";

import { appPath } from "@/lib/paths";
import { unreachableMessage } from "@/lib/http";
import { type FormEvent, useEffect, useState } from "react";
import { Check } from "lucide-react";
import { useRouter } from "next/navigation";
import { agentLanguages, type AgentLanguage, type DeviceRule, type WindowsUserAccount } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { SwitchField } from "@/components/ui/switch";

type FormValues = {
  controlledUserSid: string;
  language: AgentLanguage;
  openWindowAtSignIn: boolean;
  idleMinutes: string;
};

function valuesOf(rule: DeviceRule): FormValues {
  return {
    controlledUserSid: rule.controlledUserSid ?? "",
    language: rule.language ?? "English",
    openWindowAtSignIn: rule.openWindowAtSignIn,
    idleMinutes: String(Math.round(rule.idleThresholdSeconds / 60)),
  };
}

function sameValues(left: FormValues, right: FormValues) {
  return left.controlledUserSid === right.controlledUserSid
    && left.language === right.language
    && left.openWindowAtSignIn === right.openWindowAtSignIn
    && Number(left.idleMinutes) === Number(right.idleMinutes);
}

/**
 * Everything about the PC's rule except its schedule and daily limit, which the schedule card
 * above edits in popups and saves on its own - so this form neither sends them nor overwrites them.
 *
 * The form is long and the page keeps going below it, so its save button floats at the foot of
 * the window whenever something has changed, rather than waiting at the end of the form for a
 * parent who changed the account at the top and scrolled on.
 */
export function DeviceRuleEditor({ deviceId, rule, accounts }: { deviceId: string; rule: DeviceRule; accounts: WindowsUserAccount[] }) {
  const router = useRouter();
  const stored = valuesOf(rule);
  const [base, setBase] = useState(stored);
  const [values, setValues] = useState(stored);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  // The page refreshes itself every thirty seconds. A rule that changed underneath an untouched
  // form replaces it; one that changed under an edit in progress leaves the edit alone.
  if (!sameValues(stored, base)) {
    setBase(stored);
    if (sameValues(values, base)) setValues(stored);
  }
  const dirty = !sameValues(values, base);

  useEffect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [dirty]);

  function change(patch: Partial<FormValues>) {
    setValues(current => ({ ...current, ...patch }));
    setSaved(false);
    setError("");
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaved(false);
    setError("");
    setBusy(true);
    try {
      const response = await fetch(appPath(`/api/backend/devices/${deviceId}/rules`), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          controlledUserSid: values.controlledUserSid || null,
          language: values.language,
          openWindowAtSignIn: values.openWindowAtSignIn,
          idleThresholdSeconds: Number(values.idleMinutes) * 60,
        }),
      });
      if (!response.ok) {
        setError(response.status === 400 ? "Check the idle value, then try again." : "The PC rule could not be saved.");
        return;
      }
      setBase(values);
      setSaved(true);
      setTimeout(() => setSaved(false), 4000);
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
              value={values.controlledUserSid}
              onChange={event => change({ controlledUserSid: event.target.value })}
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
              value={values.language}
              onChange={event => change({ language: event.target.value as AgentLanguage })}
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
            checked={values.openWindowAtSignIn}
            onCheckedChange={checked => change({ openWindowAtSignIn: checked })}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Idle detection</CardTitle><CardDescription>Usage pauses after this long without keyboard or mouse input.</CardDescription></CardHeader>
        <CardContent><Field className="max-w-48"><FieldLabel htmlFor="idleMinutes">Minutes until idle</FieldLabel><Input id="idleMinutes" name="idleMinutes" type="number" min="1" max="60" value={values.idleMinutes} onChange={event => change({ idleMinutes: event.target.value })} /></Field></CardContent>
      </Card>

      {(dirty || busy || saved || error) && (
        <div className="pointer-events-none fixed inset-x-0 bottom-0 z-30 flex justify-center p-4 lg:left-60">
          <div
            role="status"
            aria-live="polite"
            className="pointer-events-auto flex w-full max-w-xl flex-wrap items-center gap-3 rounded-xl border bg-background/95 px-4 py-3 shadow-lg backdrop-blur animate-in fade-in-0 slide-in-from-bottom-4"
          >
            <p className="min-w-0 flex-1 text-sm">
              {error ? <span className="text-destructive">{error}</span>
                : saved && !dirty ? <span className="flex items-center gap-1.5 text-primary"><Check className="size-4" />Saved. The PC picks it up with the next sync.</span>
                : "You have unsaved changes."}
            </p>
            {dirty && (
              <div className="flex shrink-0 items-center gap-2">
                <Button type="button" variant="ghost" disabled={busy} onClick={() => { setValues(base); setError(""); }}>Discard</Button>
                <Button type="submit" size="lg" disabled={busy}>{busy ? "Saving…" : "Save changes"}</Button>
              </div>
            )}
          </div>
        </div>
      )}
    </form>
  );
}
