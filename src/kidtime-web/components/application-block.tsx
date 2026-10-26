"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { Ban, CircleCheck } from "lucide-react";
import { appPath } from "@/lib/paths";
import { Button } from "@/components/ui/button";
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

/**
 * Blocks or unblocks one application, saved at once. Blocking asks first - it closes the
 * application on the child's screen after a warning - and unblocking does not, like the PC's own
 * block. The block also holds offline, from the cached rules.
 */
export function ApplicationBlock({ applicationId, displayName, blocked }: { applicationId: string; displayName: string; blocked: boolean }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function set(manuallyBlocked: boolean) {
    setBusy(true);
    setError("");
    try {
      const response = await fetch(appPath(`/api/backend/applications/${applicationId}/rules`), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ manuallyBlocked }),
      });
      if (!response.ok) {
        setError("The change could not be saved.");
        return;
      }
      router.refresh();
    } catch {
      setError("Could not reach the KidTime server.");
    } finally {
      setBusy(false);
    }
  }

  if (blocked) {
    return (
      <div className="flex flex-col items-end gap-1">
        <Button variant="outline" onClick={() => set(false)} disabled={busy}>
          <CircleCheck data-icon="inline-start" />{busy ? "Sending…" : "Unblock"}
        </Button>
        {error && <p className="text-xs text-destructive">{error}</p>}
      </div>
    );
  }

  return (
    <div className="flex flex-col items-end gap-1">
      <AlertDialog>
        <AlertDialogTrigger render={<Button variant="destructive" disabled={busy} />}>
          <Ban data-icon="inline-start" />Block
        </AlertDialogTrigger>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogMedia><Ban /></AlertDialogMedia>
            <AlertDialogTitle>Block {displayName}?</AlertDialogTitle>
            <AlertDialogDescription>If it is open, the child gets a warning and it closes. It keeps closing whenever it starts until you unblock it.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={() => set(true)} disabled={busy}>{busy ? "Sending…" : "Block"}</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      {error && <p className="text-xs text-destructive">{error}</p>}
    </div>
  );
}
