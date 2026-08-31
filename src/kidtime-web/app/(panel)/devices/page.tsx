import Link from "next/link";
import { ChevronRight, Monitor, TriangleAlert } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration, formatSeen } from "@/lib/format";
import { describeSchedule, describeTodayWindows } from "@/lib/schedule";
import type { DeviceSummary } from "@/lib/types";
import { EmptyDevices } from "@/components/empty-state";
import { PageHeader } from "@/components/page-header";
import { QuickBlock } from "@/components/quick-block";
import { ScheduleStrip } from "@/components/schedule-strip";
import { StatusBadge } from "@/components/status-badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Badge } from "@/components/ui/badge";
import { AddDevice } from "@/components/add-device";

export default async function DevicesPage() {
  const devices = await backendFetch<DeviceSummary[]>("/api/devices");
  return (
    <>
      <PageHeader eyebrow="Control" title="Devices" description="Limits, sync state, and current activity for enrolled Windows PCs." actions={<AddDevice />} />
      {devices.length === 0 ? <EmptyDevices /> : (
        <div className="grid gap-4">
          {devices.map(device => {
            const percent = device.dailyLimitSeconds ? Math.min(100, device.todayActiveSeconds / device.dailyLimitSeconds * 100) : 0;
            const schedule = describeSchedule(device);
            const todayWindows = describeTodayWindows(device);
            return (
              <Card key={device.id}>
                <CardHeader>
                  <span className="flex size-10 items-center justify-center rounded-lg bg-primary/10 text-primary"><Monitor className="size-5" /></span>
                  <CardTitle className="mt-2 flex flex-wrap items-center gap-2">
                    {device.name}<StatusBadge online={device.isOnline} />
                    <Badge variant={device.isAgentUpToDate ? "secondary" : "destructive"} className={device.isAgentUpToDate ? "text-primary" : undefined}>
                      {device.isAgentUpToDate ? `Services v${device.agentVersion} current` : device.agentUpdateStatus === "Installing" ? "Services updating" : `Services outdated${device.latestAgentVersion ? ` · v${device.latestAgentVersion} available` : ""}`}
                    </Badge>
                    {device.unresolvedFaults > 0 && (
                      <Badge variant="destructive" render={<Link href={`/diagnostics?deviceId=${device.id}`} />}>
                        <TriangleAlert data-icon="inline-start" />
                        {device.unresolvedFaults === 1 ? "1 error reported" : `${device.unresolvedFaults} errors reported`}
                      </Badge>
                    )}
                  </CardTitle>
                  <CardDescription>{device.windowsVersion} · Last seen {formatSeen(device.lastSeenUtc)}</CardDescription>
                  <CardAction className="flex items-center gap-2">
                    <QuickBlock deviceId={device.id} blocked={device.manuallyBlocked} />
                    <Button variant="outline" size="icon" render={<Link href={`/devices/${device.id}`} aria-label={`Settings for ${device.name}`} />}><ChevronRight /></Button>
                  </CardAction>
                </CardHeader>
                <CardContent className="grid gap-5 md:grid-cols-[1fr_200px_180px] md:items-end">
                  <div>
                    {device.dailyLimitSeconds ? (
                      <>
                        <div className="mb-2 flex justify-between text-xs text-muted-foreground tabular-nums"><span>Today {formatDuration(device.todayActiveSeconds)} of {formatDuration(device.dailyLimitSeconds)}</span><span>{formatDuration(device.remainingSeconds)} left</span></div>
                        <Progress value={percent} aria-label={`${Math.round(percent)} percent of daily PC limit used`} />
                      </>
                    ) : (
                      <>
                        <p className="text-xs text-muted-foreground">Today</p>
                        <p className="mt-1 text-sm font-medium tabular-nums">{formatDuration(device.todayActiveSeconds)}<span className="ml-2 font-normal text-muted-foreground">no daily limit</span></p>
                      </>
                    )}
                  </div>
                  <div>
                    <p className="text-xs text-muted-foreground">{schedule.label}</p>
                    <p className="mt-1 text-sm font-medium tabular-nums">{schedule.value}</p>
                  </div>
                  {/* Nothing is in the foreground of a PC that is not reporting, so an offline card says so rather than repeating what was open last. */}
                  <div><p className="text-xs text-muted-foreground">Current application</p><p className={device.isOnline ? "mt-1 truncate text-sm font-medium" : "mt-1 truncate text-sm text-muted-foreground"}>{device.isOnline ? device.foregroundApplication ?? "None" : "Not reporting"}</p></div>
                  {device.schedule.configured && (
                    <div className="md:col-span-3">
                      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
                        <p className="text-xs text-muted-foreground tabular-nums">{todayWindows}</p>
                        <Badge variant={device.schedule.withinWindow ? "secondary" : "outline"} className={device.schedule.withinWindow ? "text-primary" : "text-muted-foreground"}>
                          {device.schedule.withinWindow ? "Open now" : "Closed now"}
                        </Badge>
                      </div>
                      <ScheduleStrip windows={device.schedule.todayWindows} nowMinute={device.schedule.nowMinuteOfDay} />
                    </div>
                  )}
                </CardContent>
              </Card>
            );
          })}
        </div>
      )}
    </>
  );
}
