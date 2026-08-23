"use client";

import { useState } from "react";
import { Check, Copy, KeyRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function EnrollmentToken() {
  const [token, setToken] = useState("");
  const [expires, setExpires] = useState("");
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);
  async function create() {
    setBusy(true);
    const response = await fetch("/api/backend/enrollment-tokens", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ validForMinutes: 30 }) });
    const data = await response.json();
    setToken(data.token ?? ""); setExpires(data.expiresAtUtc ?? ""); setBusy(false); setCopied(false);
  }
  async function copy() { await navigator.clipboard.writeText(token); setCopied(true); }
  return (
    <Card>
      <CardHeader>
        <CardTitle>Enrollment token</CardTitle>
        <CardDescription>Single-use credential for adding a Windows PC. It expires after 30 minutes.</CardDescription>
        <CardAction><span className="flex size-9 items-center justify-center rounded-lg bg-primary/10 text-primary"><KeyRound className="size-4" /></span></CardAction>
      </CardHeader>
      <CardContent>
        {token ? (
          <div className="rounded-lg border bg-muted/50 p-3">
            <div className="flex items-center gap-2"><code className="min-w-0 flex-1 break-all text-xs font-medium">{token}</code><Button variant="outline" size="icon" onClick={copy} aria-label="Copy token">{copied ? <Check /> : <Copy />}</Button></div>
            <p className="mt-2 text-xs text-muted-foreground">Expires {new Date(expires).toLocaleTimeString()}</p>
          </div>
        ) : <Button onClick={create} disabled={busy}>{busy ? "Creating…" : "Create enrollment token"}</Button>}
      </CardContent>
    </Card>
  );
}
