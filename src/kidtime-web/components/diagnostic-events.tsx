"use client";

import { appPath } from "@/lib/paths";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { AlertTriangle, Check, ChevronDown, CircleX, Trash2, TriangleAlert } from "lucide-react";
import type { DiagnosticEvent } from "@/lib/types";
import { formatSeen } from "@/lib/format";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";

const severityIcon = {
  Fatal: CircleX,
  Error: TriangleAlert,
  Warning: AlertTriangle,
} as const;

function severityVariant(severity: string) {
  return severity === "Warning" ? "secondary" : "destructive";
}

export function DiagnosticEvents({ events }: { events: DiagnosticEvent[] }) {
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);

  async function act(id: string, action: "resolve" | "delete") {
    setBusy(id);
    try {
      await fetch(
        appPath(action === "resolve" ? `/api/backend/diagnostics/${id}/resolve` : `/api/backend/diagnostics/${id}`),
        { method: action === "resolve" ? "POST" : "DELETE" },
      );
      router.refresh();
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="grid gap-3">
      {events.map(event => {
        const Icon = severityIcon[event.severity as keyof typeof severityIcon] ?? TriangleAlert;
        const open = expanded === event.id;
        return (
          <Card key={event.id} className={event.resolvedAtUtc ? "opacity-70" : undefined}>
            <CardContent className="grid gap-3">
              <div className="flex flex-wrap items-start gap-3">
                <span className={event.severity === "Warning"
                  ? "mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-lg bg-muted text-muted-foreground"
                  : "mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-lg bg-destructive/10 text-destructive"}>
                  <Icon className="size-4" />
                </span>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium break-words">{event.message}</p>
                  <div className="mt-1.5 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                    <Badge variant={severityVariant(event.severity)}>{event.severity}</Badge>
                    <Badge variant="outline">{event.component}</Badge>
                    <span>{event.deviceName}</span>
                    <span aria-hidden="true">·</span>
                    <span>Last seen {formatSeen(event.lastOccurredAtUtc)}</span>
                    {event.occurrenceCount > 1 && (
                      <>
                        <span aria-hidden="true">·</span>
                        <span>{event.occurrenceCount} times</span>
                      </>
                    )}
                    {event.agentVersion && (
                      <>
                        <span aria-hidden="true">·</span>
                        <span>v{event.agentVersion}</span>
                      </>
                    )}
                    {event.resolvedAtUtc && <Badge variant="secondary">Resolved</Badge>}
                  </div>
                  {event.exceptionType && (
                    <p className="mt-1.5 font-mono text-xs break-all text-muted-foreground">{event.exceptionType}</p>
                  )}
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  {event.detail && (
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-expanded={open}
                      onClick={() => setExpanded(open ? null : event.id)}
                    >
                      <ChevronDown className={open ? "rotate-180 transition-transform" : "transition-transform"} />
                      Details
                    </Button>
                  )}
                  {!event.resolvedAtUtc && (
                    <Button variant="outline" size="sm" disabled={busy === event.id} onClick={() => act(event.id, "resolve")}>
                      <Check data-icon="inline-start" />Mark handled
                    </Button>
                  )}
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Delete this report"
                    disabled={busy === event.id}
                    onClick={() => act(event.id, "delete")}
                  >
                    <Trash2 />
                  </Button>
                </div>
              </div>
              {open && event.detail && (
                <pre className="max-h-80 overflow-auto rounded-lg bg-muted/60 p-3 font-mono text-xs whitespace-pre-wrap">
                  {event.detail}
                </pre>
              )}
            </CardContent>
          </Card>
        );
      })}
    </div>
  );
}

export function ResolveAllFaults({ deviceId }: { deviceId?: string }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);

  async function resolveAll() {
    setBusy(true);
    try {
      await fetch(appPath(`/api/backend/diagnostics/resolve${deviceId ? `?deviceId=${deviceId}` : ""}`), {
        method: "POST",
      });
      router.refresh();
    } finally {
      setBusy(false);
    }
  }

  return (
    <Button variant="outline" onClick={resolveAll} disabled={busy}>
      <Check data-icon="inline-start" />{busy ? "Marking…" : "Mark all handled"}
    </Button>
  );
}
