"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { AppWindow, Check, MonitorCog, X } from "lucide-react";
import { appPath } from "@/lib/paths";
import { formatSeen } from "@/lib/format";
import type { TimeExtensionRequest } from "@/lib/types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Slider } from "@/components/ui/slider";

// The same stops the child's slider has, so an approval is always an amount they could have
// asked for and there is nothing between the marks to negotiate over.
const MINIMUM_MINUTES = 5;
const MAXIMUM_MINUTES = 30;
const STEP_MINUTES = 5;
const TICKS = Array.from(
  { length: (MAXIMUM_MINUTES - MINIMUM_MINUTES) / STEP_MINUTES + 1 },
  (_, index) => MINIMUM_MINUTES + index * STEP_MINUTES,
);

function clampToStep(minutes: number) {
  const stepped = Math.round(minutes / STEP_MINUTES) * STEP_MINUTES;
  return Math.min(MAXIMUM_MINUTES, Math.max(MINIMUM_MINUTES, stepped));
}

/**
 * One pending request, with the decision beside it. The slider starts on what the child asked
 * for, so "yes, but twenty minutes" is one drag and one click rather than a conversation.
 */
function PendingRequest({ request }: { request: TimeExtensionRequest }) {
  const router = useRouter();
  const [minutes, setMinutes] = useState(clampToStep(request.requestedMinutes));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function decide(approved: boolean) {
    setBusy(true);
    setError(null);
    const response = await fetch(appPath(`/api/backend/time-extensions/${request.id}/decision`), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ approved, minutes: approved ? minutes : null }),
    });
    if (!response.ok) {
      // The API answers a rejected decision with a bare JSON string, so the quotes come off.
      const body = (await response.text()).trim().replace(/^"|"$/g, "");
      setError(body || "The decision could not be saved.");
      setBusy(false);
      return;
    }
    router.refresh();
    setBusy(false);
  }

  return (
    <Card>
      <CardContent className="flex flex-col gap-4 py-5 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex min-w-0 items-start gap-3">
          <span className="mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground">
            {request.isPc ? <MonitorCog className="size-4" /> : <AppWindow className="size-4" />}
          </span>
          <div className="min-w-0">
            <p className="font-medium">
              {request.deviceName} · asked for {request.requestedMinutes} more minutes
            </p>
            <p className="text-sm text-muted-foreground">
              {request.isPc ? "PC screen time" : request.displayName} · {formatSeen(request.requestedAtUtc)}
            </p>
            {error && <p className="mt-1 text-sm text-destructive">{error}</p>}
          </div>
        </div>
        <div className="flex flex-wrap items-end gap-4">
          <div className="w-52 shrink-0">
            <div className="mb-1 flex items-baseline justify-between">
              <span className="text-xs text-muted-foreground">Grant</span>
              <span className="text-sm font-semibold tabular-nums">{minutes} min</span>
            </div>
            <Slider
              min={MINIMUM_MINUTES}
              max={MAXIMUM_MINUTES}
              step={STEP_MINUTES}
              ticks={TICKS}
              value={minutes}
              onValueChange={value => setMinutes(clampToStep(value))}
              disabled={busy}
              aria-label="Minutes to grant"
            />
          </div>
          <div className="flex items-center gap-2">
            <Button onClick={() => decide(true)} disabled={busy}>
              <Check data-icon="inline-start" />Approve
            </Button>
            <Button variant="outline" onClick={() => decide(false)} disabled={busy}>
              <X data-icon="inline-start" />Deny
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

export function TimeExtensionRequests({ requests }: { requests: TimeExtensionRequest[] }) {
  const pending = requests.filter(request => request.status === "Pending");
  const decided = requests.filter(request => request.status !== "Pending");

  return (
    <div className="flex flex-col gap-6">
      {pending.length > 0 && (
        <div className="flex flex-col gap-3">
          {pending.map(request => <PendingRequest key={request.id} request={request} />)}
        </div>
      )}
      {decided.length > 0 && (
        <Card>
          <CardContent className="flex flex-col gap-3 py-5">
            <h2 className="text-sm font-medium text-muted-foreground">Recently decided</h2>
            {decided.map(request => (
              <div key={request.id} className="flex flex-wrap items-center justify-between gap-2 text-sm">
                <span className="text-muted-foreground">
                  {request.deviceName} · {request.isPc ? "PC screen time" : request.displayName} ·{" "}
                  asked for {request.requestedMinutes}m on {request.localDate}
                </span>
                <Badge variant={request.status === "Approved" ? "secondary" : "outline"}>
                  {request.status === "Approved" ? `Granted ${request.grantedMinutes}m` : "Denied"}
                </Badge>
              </div>
            ))}
          </CardContent>
        </Card>
      )}
    </div>
  );
}
