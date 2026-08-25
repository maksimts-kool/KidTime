import Link from "next/link";
import { ShieldCheck } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import type { DiagnosticEvent } from "@/lib/types";
import { DiagnosticEvents, ResolveAllFaults } from "@/components/diagnostic-events";
import { PageHeader } from "@/components/page-header";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";

export default async function DiagnosticsPage({
  searchParams,
}: {
  searchParams: Promise<{ deviceId?: string; resolved?: string }>;
}) {
  const { deviceId, resolved } = await searchParams;
  const includeResolved = resolved === "1";
  const query = new URLSearchParams();
  if (deviceId) query.set("deviceId", deviceId);
  if (includeResolved) query.set("includeResolved", "true");
  const events = await backendFetch<DiagnosticEvent[]>(
    `/api/diagnostics${query.size ? `?${query}` : ""}`,
  );
  const open = events.filter(event => !event.resolvedAtUtc);

  return (
    <>
      <PageHeader
        eyebrow="Diagnostics"
        title="Error log"
        description="Crashes and errors reported by the KidTime service and tray agent on enrolled PCs. Reports arrive with the next synchronization, so an offline PC delivers them when it reconnects."
        actions={(
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="ghost" render={<Link href={includeResolved ? "/diagnostics" : "/diagnostics?resolved=1"} />}>
              {includeResolved ? "Hide handled" : "Show handled"}
            </Button>
            {open.length > 0 && <ResolveAllFaults deviceId={deviceId} />}
          </div>
        )}
      />
      {events.length === 0 ? (
        <Card>
          <CardContent className="flex min-h-80 flex-col items-center justify-center px-6 text-center">
            <span className="mb-4 flex size-12 items-center justify-center rounded-full bg-primary/10 text-primary"><ShieldCheck className="size-5" /></span>
            <h2 className="font-medium">No faults reported</h2>
            <p className="mt-1 max-w-md text-sm text-muted-foreground">
              {includeResolved
                ? "No PC has reported an error yet."
                : "Nothing needs attention. Handled reports are hidden."}
            </p>
          </CardContent>
        </Card>
      ) : (
        <DiagnosticEvents events={events} />
      )}
    </>
  );
}
