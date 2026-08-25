"use client";

import { appPath } from "@/lib/paths";
import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import {
  ArrowLeft,
  ArrowRight,
  Check,
  CheckCircle2,
  Clipboard,
  Download,
  LoaderCircle,
  MonitorCog,
  Plus,
  RefreshCw,
  ShieldCheck,
  TriangleAlert,
} from "lucide-react";
import { formatBytes } from "@/lib/format";
import { cn } from "@/lib/utils";
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

type Installer = {
  available: boolean;
  fileName: string | null;
  version: string | null;
  sizeBytes: number | null;
  sha256: string | null;
};

type Enrollment = {
  id: string;
  token: string;
  expiresAtUtc: string;
  serverUrl: string | null;
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

const STEPS = ["Download setup", "Copy the code", "Connect"];

export function AddDevice({ emptyState = false }: { emptyState?: boolean }) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [step, setStep] = useState(0);
  const [installer, setInstaller] = useState<Installer | null>(null);
  const [downloaded, setDownloaded] = useState(false);
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null);
  const [status, setStatus] = useState<EnrollmentStatus>({ status: "pending" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [copied, setCopied] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const enrollmentId = enrollment?.id;
  const connected = status.status === "connected" && status.device;
  const serverUrl = useMemo(
    () => enrollment?.serverUrl?.trim() || (typeof window === "undefined" ? "" : `https://${window.location.hostname}:5081`),
    [enrollment],
  );
  const setupCode = useMemo(
    () => (enrollment ? buildSetupCode(serverUrl, enrollment.token) : null),
    [enrollment, serverUrl],
  );
  const expiresInSeconds = enrollment
    ? Math.max(0, Math.round((new Date(enrollment.expiresAtUtc).getTime() - now) / 1000))
    : 0;
  const expired = Boolean(enrollment) && (status.status === "expired" || expiresInSeconds === 0);

  function reset() {
    setStep(0);
    setInstaller(null);
    setDownloaded(false);
    setEnrollment(null);
    setStatus({ status: "pending" });
    setError("");
    setCopied(null);
  }

  async function changeOpen(value: boolean) {
    setOpen(value);
    if (!value) {
      reset();
      return;
    }
    reset();
    const response = await fetch(appPath("/api/backend/installer"), { cache: "no-store" }).catch(() => null);
    if (!response?.ok) {
      setError("KidTime could not reach the server. Check that it is running, then try again.");
      return;
    }
    setInstaller(await response.json() as Installer);
  }

  const createToken = useCallback(async () => {
    setBusy(true);
    setError("");
    const response = await fetch(appPath("/api/backend/enrollment-tokens"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ validForMinutes: 30 }),
    }).catch(() => null);
    setBusy(false);
    if (!response?.ok) {
      setError("KidTime could not create an enrollment code. Check that the server is running and try again.");
      return false;
    }
    setEnrollment(await response.json() as Enrollment);
    setStatus({ status: "pending" });
    return true;
  }, []);

  useEffect(() => {
    if (!open) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [open]);

  useEffect(() => {
    if (!open || !enrollmentId || status.status === "connected" || status.status === "expired") return;
    let cancelled = false;
    async function check() {
      const response = await fetch(appPath(`/api/backend/enrollment-tokens/${enrollmentId}`), { cache: "no-store" }).catch(() => null);
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

  async function goNext() {
    if (step === 0 && !enrollment) {
      const created = await createToken();
      if (!created) return;
    }
    setStep(current => Math.min(STEPS.length - 1, current + 1));
  }

  async function copy(value: string, kind: string) {
    await navigator.clipboard.writeText(value);
    setCopied(kind);
    window.setTimeout(() => setCopied(current => current === kind ? null : current), 1800);
  }

  return (
    <AlertDialog open={open} onOpenChange={changeOpen}>
      <AlertDialogTrigger render={<Button size={emptyState ? "lg" : "default"} />}>
        <Plus data-icon="inline-start" />Add device
      </AlertDialogTrigger>
      <AlertDialogContent className="max-h-[92vh] overflow-y-auto sm:max-w-2xl">
        {connected && status.device ? (
          <>
            <AlertDialogHeader>
              <span className="mb-1 flex size-11 items-center justify-center rounded-full bg-primary/10 text-primary"><CheckCircle2 className="size-6" /></span>
              <AlertDialogTitle>{status.device.name} is connected</AlertDialogTitle>
              <AlertDialogDescription>
                KidTime is running{status.device.controlledUserName ? ` and controlling ${status.device.controlledUserName}` : ""}. This PC is ready for limits, schedules, and app rules.
              </AlertDialogDescription>
            </AlertDialogHeader>
            <div className="rounded-xl border bg-primary/5 p-4 text-sm">
              <p className="flex items-center gap-2 font-medium text-primary"><ShieldCheck className="size-4" /> Secure enrollment complete</p>
              <p className="mt-1 text-muted-foreground">The one-time code has been used and can never connect another PC.</p>
            </div>
            <AlertDialogFooter>
              <AlertDialogCancel>Close</AlertDialogCancel>
              <Button render={<Link href={`/devices/${status.device.id}`} />}>Open device controls</Button>
            </AlertDialogFooter>
          </>
        ) : (
          <>
            <AlertDialogHeader>
              <AlertDialogTitle>Add a Windows PC</AlertDialogTitle>
              <AlertDialogDescription>Three steps, all from the web panel. No commands or scripts are needed on either PC.</AlertDialogDescription>
            </AlertDialogHeader>

            <Stepper current={step} onSelect={index => index < step && setStep(index)} />

            {error ? (
              <div className="flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
                <TriangleAlert className="mt-0.5 size-4 shrink-0" />{error}
              </div>
            ) : null}

            {step === 0 && (
              <StepPanel
                title="Download KidTime Setup"
                description="Get the setup file here, then copy it to the child’s PC and open it. Windows asks for administrator approval once."
              >
                {installer === null ? (
                  <p className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="size-4 animate-spin" /> Checking the published setup file…</p>
                ) : installer.available ? (
                  <>
                    <Button
                      variant="outline"
                      onClick={() => setDownloaded(true)}
                      render={<a href={appPath("/api/backend/installer/download")} download={installer.fileName ?? "KidTimeSetup.exe"} />}
                    >
                      <Download data-icon="inline-start" />Download setup (.exe)
                    </Button>
                    <dl className="mt-3 grid gap-1 text-xs text-muted-foreground">
                      <div className="flex gap-2"><dt className="w-20 shrink-0">Version</dt><dd className="font-medium text-foreground">{installer.version ?? "Unknown"}</dd></div>
                      <div className="flex gap-2"><dt className="w-20 shrink-0">Size</dt><dd className="font-medium text-foreground">{formatBytes(installer.sizeBytes)}</dd></div>
                      {installer.sha256 && (
                        <div className="flex gap-2"><dt className="w-20 shrink-0">SHA-256</dt><dd className="min-w-0 break-all">{installer.sha256}</dd></div>
                      )}
                    </dl>
                    {downloaded && (
                      <p className="mt-3 flex items-center gap-2 text-xs text-primary"><Check className="size-3.5" /> Download started. Move the file to the child’s PC and open it.</p>
                    )}
                  </>
                ) : (
                  <p className="rounded-lg bg-destructive/10 p-3 text-sm text-destructive">
                    This server has not published a setup file yet. Build the agent on the Windows build machine, upload the release to the server, publish it with <code className="font-medium">scripts/publish-agent-release.sh</code>, then reopen this window.
                  </p>
                )}
              </StepPanel>
            )}

            {step === 1 && (
              <StepPanel
                title="Copy the setup code"
                description="Setup asks for these details before it can connect. The setup code fills in both at once; the separate values are there if you prefer to type them."
              >
                {busy || !enrollment ? (
                  <p className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="size-4 animate-spin" /> Creating a one-time code…</p>
                ) : expired ? (
                  <div className="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm">
                    <p className="font-medium text-destructive">This code has expired.</p>
                    <p className="mt-1 text-muted-foreground">Codes last 30 minutes so an unused one cannot be reused later.</p>
                    <Button className="mt-3" variant="outline" onClick={createToken} disabled={busy}>
                      <RefreshCw data-icon="inline-start" />Create a new code
                    </Button>
                  </div>
                ) : (
                  <>
                    {setupCode && <CopyField label="Setup code" value={setupCode} copied={copied === "setup"} onCopy={() => copy(setupCode, "setup")} mono />}
                    <div className="mt-3 grid gap-3 sm:grid-cols-2">
                      <CopyField label="Server URL" value={serverUrl} copied={copied === "url"} onCopy={() => copy(serverUrl, "url")} />
                      <CopyField label="Enrollment code" value={enrollment.token} copied={copied === "token"} onCopy={() => copy(enrollment.token, "token")} mono />
                    </div>
                    <p className="mt-3 text-xs text-muted-foreground">
                      Expires in {formatCountdown(expiresInSeconds)}. Keep it private: it is single-use and carries this server’s certificate fingerprint.
                    </p>
                  </>
                )}
              </StepPanel>
            )}

            {step === 2 && (
              <StepPanel
                title="Choose the child account in Setup"
                description="On the child’s PC, paste the code into Setup, pick the child’s standard Windows account, and select Connect. Keep this window open."
              >
                {expired ? (
                  <div className="flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
                    <TriangleAlert className="mt-0.5 size-4 shrink-0" />
                    <span>The code expired before this PC connected. Go back a step and create a new one.</span>
                  </div>
                ) : (
                  <div className="flex items-center gap-3 rounded-lg bg-muted/60 px-3 py-3 text-sm">
                    <LoaderCircle className="size-4 shrink-0 animate-spin text-primary" />
                    <span>{status.status === "finishing" ? "This PC enrolled. Waiting for the KidTime service to start…" : "Waiting for this PC to connect…"}</span>
                  </div>
                )}
                <ul className="mt-4 grid gap-2 text-sm text-muted-foreground">
                  <li className="flex gap-2"><MonitorCog className="mt-0.5 size-4 shrink-0" />Setup installs the KidTime service, protects its files from the child account, and starts it.</li>
                  <li className="flex gap-2"><ShieldCheck className="mt-0.5 size-4 shrink-0" />Administrator and disabled Windows accounts are never offered as the controlled profile.</li>
                </ul>
              </StepPanel>
            )}

            <AlertDialogFooter className="sm:justify-between">
              <AlertDialogCancel>Cancel</AlertDialogCancel>
              <div className="flex gap-2 sm:justify-end">
                {step > 0 && (
                  <Button variant="outline" onClick={() => setStep(current => current - 1)} disabled={busy}>
                    <ArrowLeft data-icon="inline-start" />Back
                  </Button>
                )}
                {step < STEPS.length - 1 && (
                  <Button onClick={goNext} disabled={busy}>
                    {busy ? <LoaderCircle className="animate-spin" data-icon="inline-start" /> : null}
                    Next<ArrowRight data-icon="inline-end" />
                  </Button>
                )}
              </div>
            </AlertDialogFooter>
          </>
        )}
      </AlertDialogContent>
    </AlertDialog>
  );
}

function Stepper({ current, onSelect }: { current: number; onSelect: (index: number) => void }) {
  return (
    <ol className="flex items-center gap-2">
      {STEPS.map((label, index) => {
        const done = index < current;
        const active = index === current;
        return (
          <li key={label} className={cn("flex items-center gap-2", index < STEPS.length - 1 && "flex-1")}>
            <button
              type="button"
              onClick={() => onSelect(index)}
              disabled={!done}
              aria-current={active ? "step" : undefined}
              className={cn(
                "flex size-6 shrink-0 items-center justify-center rounded-full text-xs font-semibold transition-colors",
                done ? "bg-primary/15 text-primary hover:bg-primary/25" : active ? "bg-primary text-primary-foreground" : "bg-muted text-muted-foreground",
              )}
            >
              {done ? <Check className="size-3.5" /> : index + 1}
            </button>
            <span className={cn("truncate text-xs", done || active ? "font-medium text-foreground" : "text-muted-foreground")}>{label}</span>
            {index < STEPS.length - 1 && <span className={cn("h-px flex-1", done ? "bg-primary/40" : "bg-border")} />}
          </li>
        );
      })}
    </ol>
  );
}

function StepPanel({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  return (
    <section className="rounded-xl border p-4">
      <h3 className="font-medium">{title}</h3>
      <p className="mt-1 mb-4 text-sm text-muted-foreground">{description}</p>
      {children}
    </section>
  );
}

function CopyField({ label, value, copied, onCopy, mono = false }: { label: string; value: string; copied: boolean; onCopy: () => void; mono?: boolean }) {
  return (
    <div>
      <p className="mb-1 text-xs font-medium text-muted-foreground">{label}</p>
      <div className="flex items-start gap-2 rounded-lg border bg-muted/30 p-2">
        <code className={cn("min-w-0 flex-1 self-center text-xs break-all", mono ? "leading-relaxed" : "font-medium")}>{value}</code>
        <Button variant="outline" size="icon" onClick={onCopy} aria-label={`Copy ${label.toLowerCase()}`}>
          {copied ? <Check /> : <Clipboard />}
        </Button>
      </div>
    </div>
  );
}

/** Packs the server URL and the one-time code into the single value KidTime Setup accepts. */
function buildSetupCode(serverUrl: string, token: string) {
  if (!serverUrl || !token) return null;
  try {
    const encoded = btoa(serverUrl.replace(/\/+$/, "")).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    return `KTS1.${encoded}.${token}`;
  } catch {
    return null;
  }
}

function formatCountdown(totalSeconds: number) {
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return minutes > 0 ? `${minutes}m ${String(seconds).padStart(2, "0")}s` : `${seconds}s`;
}
