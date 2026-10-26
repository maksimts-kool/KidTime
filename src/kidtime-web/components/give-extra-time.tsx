"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { TimerReset } from "lucide-react";
import { appPath } from "@/lib/paths";
import { errorMessage, unreachableMessage } from "@/lib/http";
import { formatDuration } from "@/lib/format";
import type { ExtraTimeTarget } from "@/lib/types";
import { Button } from "@/components/ui/button";
import { Slider } from "@/components/ui/slider";
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogMedia,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog";

// Wider than the child's 5-30 slider: a parent giving time unasked is deciding, not answering a
// question, and an afternoon that ran long may want an hour or two. The server bounds it at four.
const MINIMUM_MINUTES = 5;
const MAXIMUM_MINUTES = 120;
const STEP_MINUTES = 5;
const DEFAULT_MINUTES = 15;

const selectClassName =
  "h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50";

/**
 * Gives extra time without waiting to be asked. It lands exactly like an approved request: the
 * minutes are added to today's limit, a block or closed schedule opens for that long from now,
 * and the child is told once. With more than one target the dialog lets the parent pick which.
 */
export function GiveExtraTime({
  targets,
  variant = "outline",
  size = "default",
}: { targets: ExtraTimeTarget[]; variant?: "outline" | "default"; size?: "default" | "xl" }) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [targetIndex, setTargetIndex] = useState(0);
  const [minutes, setMinutes] = useState(DEFAULT_MINUTES);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  if (targets.length === 0) return null;
  const target = targets[Math.min(targetIndex, targets.length - 1)];

  async function give() {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(appPath("/api/backend/time-extensions/grant"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceId: target.deviceId, deviceApplicationId: target.deviceApplicationId, minutes }),
      });
      if (!response.ok) {
        setError(await errorMessage(response, "The extra time could not be given."));
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
    <AlertDialog open={open} onOpenChange={next => { setOpen(next); if (next) { setMinutes(DEFAULT_MINUTES); setError(null); } }}>
      <AlertDialogTrigger render={<Button variant={variant} size={size} />}>
        <TimerReset data-icon="inline-start" />Give extra time
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogMedia><TimerReset /></AlertDialogMedia>
          <AlertDialogTitle>Give extra time</AlertDialogTitle>
          <AlertDialogDescription>
            Added to today&apos;s limit. If the PC or app is blocked or outside its schedule, it opens for this long from now. It reaches the PC with the next sync and ends at midnight.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <div className="grid gap-4">
          {targets.length > 1 && (
            <label className="grid gap-1.5 text-sm">
              <span className="text-xs text-muted-foreground">For</span>
              <select className={selectClassName} value={targetIndex} onChange={event => setTargetIndex(Number(event.target.value))}>
                {targets.map((item, index) => <option key={`${item.deviceId}-${item.deviceApplicationId ?? "pc"}`} value={index}>{item.label}</option>)}
              </select>
            </label>
          )}
          <div>
            <div className="mb-1 flex items-baseline justify-between">
              <span className="text-xs text-muted-foreground">{targets.length > 1 ? "Minutes" : target.label}</span>
              <span className="text-sm font-semibold tabular-nums">{formatDuration(minutes * 60)}</span>
            </div>
            <Slider
              min={MINIMUM_MINUTES}
              max={MAXIMUM_MINUTES}
              step={STEP_MINUTES}
              value={minutes}
              onValueChange={value => setMinutes(value)}
              disabled={busy}
              aria-label="Minutes to give"
            />
          </div>
          {error && <p className="text-sm text-destructive">{error}</p>}
        </div>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <Button onClick={give} disabled={busy}>{busy ? "Sending…" : `Give ${formatDuration(minutes * 60)}`}</Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
