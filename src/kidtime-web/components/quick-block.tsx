"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { LockKeyhole, UnlockKeyhole } from "lucide-react";
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

export function QuickBlock({ deviceId, blocked }: { deviceId: string; blocked: boolean }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  async function toggle() {
    setBusy(true);
    await fetch(`/api/backend/devices/${deviceId}/${blocked ? "unblock" : "block"}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: blocked ? undefined : JSON.stringify({ minutes: null, untilUtc: null }),
    });
    router.refresh();
    setBusy(false);
  }
  if (blocked) {
    return (
      <Button variant="outline" onClick={toggle} disabled={busy}>
        <UnlockKeyhole data-icon="inline-start" />{busy ? "Sending…" : "Unblock PC"}
      </Button>
    );
  }

  return (
    <AlertDialog>
      <AlertDialogTrigger render={<Button variant="destructive" disabled={busy} />}>
        <LockKeyhole data-icon="inline-start" />Block PC
      </AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogMedia><LockKeyhole /></AlertDialogMedia>
          <AlertDialogTitle>Block this PC now?</AlertDialogTitle>
          <AlertDialogDescription>The user will see a persistent countdown banner and then be signed out after 60 seconds.</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={toggle} disabled={busy}>{busy ? "Sending…" : "Block PC"}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
