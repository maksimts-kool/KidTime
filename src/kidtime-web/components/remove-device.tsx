"use client";

import { appPath } from "@/lib/paths";
import { useState } from "react";
import { Trash2 } from "lucide-react";
import { useRouter } from "next/navigation";
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

export function RemoveDevice({ deviceId, deviceName }: { deviceId: string; deviceName: string }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function remove() {
    setBusy(true);
    setError("");
    try {
      const response = await fetch(appPath(`/api/backend/devices/${deviceId}`), { method: "DELETE" });
      if (!response.ok) {
        setError("The device could not be removed. Try again.");
        return;
      }
      router.replace("/devices");
      router.refresh();
    } catch {
      setError("Could not reach the KidTime server. Try again in a moment.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <AlertDialog>
      <AlertDialogTrigger render={<Button variant="destructive" disabled={busy} />}>
        <Trash2 data-icon="inline-start" />Remove device
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogMedia><Trash2 /></AlertDialogMedia>
          <AlertDialogTitle>Remove {deviceName} from the server?</AlertDialogTitle>
          <AlertDialogDescription>
            This permanently deletes its rules, usage history, application links, and device credentials. It does not uninstall KidTime from the PC; use “Remove KidTime” in that PC&apos;s screen-time window for that.
          </AlertDialogDescription>
        </AlertDialogHeader>
        {error && <p className="text-sm text-destructive" role="alert">{error}</p>}
        <AlertDialogFooter>
          <AlertDialogCancel disabled={busy}>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={remove} disabled={busy}>
            {busy ? "Removing…" : "Remove permanently"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
