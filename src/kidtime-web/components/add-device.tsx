"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Check, CheckCircle2, Clipboard, Download, Laptop, LoaderCircle, Plus, ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog";

type Enrollment = {
  id: string;
  token: string;
  expiresAtUtc: string;
  serverUrl: string | null;
  installerAvailable: boolean;
};

type EnrollmentStatus = {
  status: "pending" | "finishing" | "connected" | "expired";
  device?: {
    id: string;
    name: string;
    controlledUserName: string | null;
    isOnline: boolean;
  };
};

export function AddDevice({ emptyState = false }: { emptyState?: boolean }) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null);
  const [status, setStatus] = useState<EnrollmentStatus>({ status: "pending" });
  const [serverUrl, setServerUrl] = useState("");
  const [error, setError] = useState("");
  const [copied, setCopied] = useState<"url" | "token" | null>(null);
  const enrollmentId = enrollment?.id;

  async function begin() {
    setEnrollment(null);
    setStatus({ status: "pending" });
    setError("");
    const response = await fetch("/api/backend/enrollment-tokens", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ validForMinutes: 30 }),
    }).catch(() => null);
    if (!response?.ok) {
      setError("KidTime could not create an enrollment token. Check that the server is running and try again.");
      return;
    }
    const data = await response.json() as Enrollment;
    const fallbackUrl = `https://${window.location.hostname}:5081`;
    setEnrollment(data);
    setServerUrl(data.serverUrl?.trim() || fallbackUrl);
  }

  function changeOpen(value: boolean) {
    setOpen(value);
    if (value) void begin();
  }

  useEffect(() => {
    if (!open || !enrollmentId || status.status === "connected" || status.status === "expired") return;
    let cancelled = false;
    async function check() {
      const response = await fetch(`/api/backend/enrollment-tokens/${enrollmentId}`, { cache: "no-store" }).catch(() => null);
      if (!response?.ok || cancelled) return;
      const next = await response.json() as EnrollmentStatus;
      if (cancelled) return;
      setStatus(next);
      if (next.status === "connected") router.refresh();
    }
    void check();
    const timer = window.setInterval(check, 2500);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [enrollmentId, open, router, status.status]);

  async function copy(value: string, kind: "url" | "token") {
    await navigator.clipboard.writeText(value);
    setCopied(kind);
    window.setTimeout(() => setCopied(current => current === kind ? null : current), 1800);
  }

  const trigger = emptyState
    ? <Button size="lg"><Plus /> Add device</Button>
    : <Button><Plus /> Add device</Button>;

  return (
    <AlertDialog open={open} onOpenChange={changeOpen}>
      <AlertDialogTrigger render={trigger} />
      <AlertDialogContent className="max-h-[92vh] overflow-y-auto sm:max-w-2xl">
        {status.status === "connected" && status.device ? (
          <>
            <AlertDialogHeader className="place-items-center text-center sm:place-items-center sm:text-center">
              <span className="mb-2 flex size-14 items-center justify-center rounded-full bg-primary/10 text-primary"><CheckCircle2 className="size-7" /></span>
              <AlertDialogTitle>{status.device.name} is connected</AlertDialogTitle>
              <AlertDialogDescription>
                KidTime is running{status.device.controlledUserName ? ` and controlling ${status.device.controlledUserName}` : ""}. The device is ready for limits, schedules, and app controls.
              </AlertDialogDescription>
            </AlertDialogHeader>
            <div className="rounded-xl border bg-primary/5 p-4 text-sm">
              <div className="flex items-center gap-2 font-medium text-primary"><ShieldCheck className="size-4" /> Secure enrollment complete</div>
              <p className="mt-1 text-muted-foreground">The one-time token has been used and cannot connect another PC.</p>
            </div>
            <AlertDialogFooter>
              <AlertDialogCancel>Close</AlertDialogCancel>
              <Button render={<Link href={`/devices/${status.device.id}`} />}>Control this device</Button>
            </AlertDialogFooter>
          </>
        ) : (
          <>
            <AlertDialogHeader>
              <AlertDialogTitle>Add a Windows device</AlertDialogTitle>
              <AlertDialogDescription>Complete these steps on the PC you want to control. No commands or scripts are needed.</AlertDialogDescription>
            </AlertDialogHeader>

            {error ? (
              <div className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">{error}</div>
            ) : !enrollment ? (
              <div className="flex min-h-48 items-center justify-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="size-4 animate-spin" /> Preparing secure setup…</div>
            ) : (
              <div className="grid gap-3">
                <section className="rounded-xl border p-4">
                  <div className="flex gap-3">
                    <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">1</span>
                    <div className="min-w-0 flex-1">
                      <h3 className="font-medium">Download KidTime Setup</h3>
                      <p className="mt-1 text-sm text-muted-foreground">Download the setup file, then move or open it on the child’s PC.</p>
                      {enrollment.installerAvailable ? (
                        <Button className="mt-3" variant="outline" render={<a href="/api/backend/installer" download />}><Download /> Download setup (.exe)</Button>
                      ) : (
                        <p className="mt-3 rounded-lg bg-destructive/10 p-2.5 text-xs text-destructive">The setup file has not been built on this server yet.</p>
                      )}
                    </div>
                  </div>
                </section>

                <section className="rounded-xl border p-4">
                  <div className="flex gap-3">
                    <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">2</span>
                    <div className="min-w-0 flex-1">
                      <h3 className="font-medium">Open setup on the child’s PC</h3>
                      <p className="mt-1 text-sm text-muted-foreground">Approve the Windows administrator prompt, then paste these two values into setup.</p>
                      <CopyField label="Server URL" value={serverUrl} copied={copied === "url"} onCopy={() => copy(serverUrl, "url")} />
                      <CopyField label="Enrollment token" value={enrollment.token} copied={copied === "token"} onCopy={() => copy(enrollment.token, "token")} secret />
                      <p className="mt-2 text-xs text-muted-foreground">Expires {new Date(enrollment.expiresAtUtc).toLocaleTimeString()}. Keep this token private.</p>
                    </div>
                  </div>
                </section>

                <section className="rounded-xl border p-4">
                  <div className="flex gap-3">
                    <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground">3</span>
                    <div className="min-w-0 flex-1">
                      <h3 className="font-medium">Choose the child user and connect</h3>
                      <p className="mt-1 text-sm text-muted-foreground">Setup lists enabled Standard User accounts. Select the child, press Connect, and keep this window open.</p>
                      <div className="mt-3 flex items-center gap-2 rounded-lg bg-muted/60 px-3 py-2.5 text-sm">
                        {status.status === "expired" ? <><Laptop className="size-4 text-destructive" /><span className="text-destructive">Token expired. Close this window and choose Add device again.</span></> : <><LoaderCircle className="size-4 animate-spin text-primary" /><span>{status.status === "finishing" ? "PC enrolled. Waiting for the KidTime service to start…" : "Waiting for the PC to connect…"}</span></>}
                      </div>
                    </div>
                  </div>
                </section>
              </div>
            )}

            <AlertDialogFooter>
              <AlertDialogCancel>Cancel</AlertDialogCancel>
              {error && <Button onClick={begin}>Try again</Button>}
            </AlertDialogFooter>
          </>
        )}
      </AlertDialogContent>
    </AlertDialog>
  );
}

function CopyField({ label, value, copied, onCopy, secret = false }: { label: string; value: string; copied: boolean; onCopy: () => void; secret?: boolean }) {
  return (
    <div className="mt-3">
      <p className="mb-1 text-xs font-medium text-muted-foreground">{label}</p>
      <div className="flex items-center gap-2 rounded-lg border bg-muted/30 p-2">
        <code className={`min-w-0 flex-1 break-all text-xs ${secret ? "leading-relaxed" : "font-medium"}`}>{value}</code>
        <Button variant="outline" size="icon" onClick={onCopy} aria-label={`Copy ${label.toLowerCase()}`}>{copied ? <Check /> : <Clipboard />}</Button>
      </div>
    </div>
  );
}
