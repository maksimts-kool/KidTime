import Link from "next/link";
import { ChevronRight, Monitor } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration, formatSeen } from "@/lib/format";
import type { DeviceSummary } from "@/lib/types";
import { EmptyDevices } from "@/components/empty-state";
import { PageHeader } from "@/components/page-header";
import { QuickBlock } from "@/components/quick-block";
import { StatusBadge } from "@/components/status-badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Badge } from "@/components/ui/badge";

export default async function DevicesPage() {
  const devices = await backendFetch<DeviceSummary[]>("/api/devices");
  return (
    <>
      <PageHeader eyebrow="Control" title="Devices" description="Limits, sync state, and current activity for enrolled Windows PCs." />
      {devices.length === 0 ? <EmptyDevices /> : (
        <div className="grid gap-4">
          {devices.map(device => {
            const percent = device.dailyLimitSeconds ? Math.min(100, device.todayActiveSeconds / device.dailyLimitSeconds * 100) : 0;
            return (
              <Card key={device.id}>
                <CardHeader>
                  <span className="flex size-10 items-center justify-center rounded-lg bg-primary/10 text-primary"><Monitor className="size-5" /></span>
                  <CardTitle className="mt-2 flex flex-wrap items-center gap-2">
                    {device.name}<StatusBadge online={device.isOnline} />
                    <Badge variant={device.isAgentUpToDate ? "secondary" : "destructive"} className={device.isAgentUpToDate ? "text-primary" : undefined}>
                      {device.isAgentUpToDate ? `Services v${device.agentVersion} current` : device.agentUpdateStatus === "Installing" ? "Services updating" : `Services outdated${device.latestAgentVersion ? ` · v${device.latestAgentVersion} available` : ""}`}
                    </Badge>
                  </CardTitle>
                  <CardDescription>{device.windowsVersion} · Last seen {formatSeen(device.lastSeenUtc)}</CardDescription>
                  <CardAction className="flex items-center gap-2">
                    <QuickBlock deviceId={device.id} blocked={device.manuallyBlocked} />
                    <Button variant="outline" size="icon" render={<Link href={`/devices/${device.id}`} aria-label={`Settings for ${device.name}`} />}><ChevronRight /></Button>
                  </CardAction>
                </CardHeader>
                <CardContent className="grid gap-5 md:grid-cols-[1fr_180px_180px] md:items-end">
                  <div>
                    <div className="mb-2 flex justify-between text-xs text-muted-foreground"><span>Today</span><span>{formatDuration(device.todayActiveSeconds)} / {formatDuration(device.dailyLimitSeconds)}</span></div>
                    <Progress value={percent} aria-label={`${Math.round(percent)} percent of daily PC limit used`} />
                  </div>
                  <div><p className="text-xs text-muted-foreground">Current application</p><p className="mt-1 truncate text-sm font-medium">{device.foregroundApplication ?? "None"}</p></div>
                  <div><p className="text-xs text-muted-foreground">Remaining</p><p className="mt-1 text-sm font-medium tabular-nums">{formatDuration(device.remainingSeconds)}</p></div>
                </CardContent>
              </Card>
            );
          })}
        </div>
      )}
    </>
  );
}
