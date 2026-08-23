import Link from "next/link";
import { ArrowRight, Clock3, Monitor, Sparkles } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration } from "@/lib/format";
import type { ApplicationSummary, DeviceStatistics, DeviceSummary } from "@/lib/types";
import { ApplicationIcon } from "@/components/application-icon";
import { EmptyDevices } from "@/components/empty-state";
import { PageHeader } from "@/components/page-header";
import { QuickBlock } from "@/components/quick-block";
import { StatCard } from "@/components/stat-card";
import { StatusBadge } from "@/components/status-badge";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Separator } from "@/components/ui/separator";

export default async function DashboardPage() {
  const devices = await backendFetch<DeviceSummary[]>("/api/devices");
  if (devices.length === 0) return <><Heading /><EmptyDevices /></>;

  const device = devices[0];
  const [statistics, applications] = await Promise.all([
    backendFetch<DeviceStatistics>(`/api/statistics/devices/${device.id}`),
    backendFetch<ApplicationSummary[]>(`/api/applications?deviceId=${device.id}`),
  ]);
  const percent = device.dailyLimitSeconds
    ? Math.min(100, device.todayActiveSeconds / device.dailyLimitSeconds * 100)
    : 0;
  const top = [...applications].sort((a, b) => b.todayActiveSeconds - a.todayActiveSeconds).slice(0, 5);
  const maxDay = Math.max(...statistics.daily.map(day => day.activeSeconds), 1);

  return (
    <>
      <Heading />
      <Card className="mb-4">
        <CardHeader className="border-b">
          <div className="flex items-center gap-2"><StatusBadge online={device.isOnline} /><span className="text-sm text-muted-foreground">{device.windowsVersion}</span></div>
          <CardTitle className="text-xl">{device.name}</CardTitle>
          <CardDescription>{device.foregroundApplication ? <>Currently using <span className="font-medium text-foreground">{device.foregroundApplication}</span></> : "No active application reported"}</CardDescription>
          <CardAction><QuickBlock deviceId={device.id} blocked={device.manuallyBlocked} /></CardAction>
        </CardHeader>
        <CardContent className="grid gap-5 pt-1 md:grid-cols-[1fr_auto] md:items-end">
          <div>
            <div className="mb-2 flex items-end justify-between gap-4">
              <div><p className="text-xs text-muted-foreground">Screen time today</p><p className="mt-1 text-2xl font-semibold tabular-nums">{formatDuration(device.todayActiveSeconds)}</p></div>
              <div className="text-right"><p className="text-xs text-muted-foreground">Daily limit</p><p className="mt-1 font-medium tabular-nums">{formatDuration(device.dailyLimitSeconds)}</p></div>
            </div>
            <Progress value={percent} aria-label={`${Math.round(percent)} percent of today's PC limit used`} />
          </div>
          <div className="min-w-40 rounded-lg bg-muted/70 px-4 py-3 md:text-right">
            <p className="text-xs text-muted-foreground">Remaining today</p>
            <p className="mt-1 text-xl font-semibold tabular-nums">{formatDuration(device.remainingSeconds)}</p>
          </div>
        </CardContent>
      </Card>

      <section className="mb-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        <StatCard label="Today’s active time" value={formatDuration(device.todayActiveSeconds)} detail="Idle time excluded" icon={Clock3} />
        <StatCard label="Last 7 days" value={formatDuration(statistics.totalActiveSeconds)} detail="Across this PC" icon={Monitor} />
        <StatCard label="Most used today" value={top[0]?.displayName ?? "Nothing yet"} detail={top[0] ? formatDuration(top[0].todayActiveSeconds) : "Usage will appear here"} icon={Sparkles} />
      </section>

      <section className="grid gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Top applications</CardTitle>
            <CardDescription>Foreground usage today</CardDescription>
            <CardAction><Button variant="ghost" size="sm" render={<Link href="/applications" />}>View all <ArrowRight data-icon="inline-end" /></Button></CardAction>
          </CardHeader>
          <CardContent>
            {top.length ? <div>{top.map((app, index) => (
              <div key={app.id}>
                {index > 0 && <Separator />}
                <Link href={`/applications/${app.id}`} className="flex items-center gap-3 rounded-lg px-1 py-3 transition-colors hover:bg-muted/50">
                  <ApplicationIcon applicationId={app.id} displayName={app.displayName} hasIcon={app.hasIcon} />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{app.displayName}</p>
                    <p className="truncate text-xs text-muted-foreground">{app.dailyLimitSeconds ? `${formatDuration(app.dailyLimitSeconds)} daily limit` : "No daily limit"}</p>
                  </div>
                  {app.manuallyBlocked && <Badge variant="destructive">Blocked</Badge>}
                  <span className="text-sm font-medium tabular-nums">{formatDuration(app.todayActiveSeconds)}</span>
                </Link>
              </div>
            ))}</div> : <p className="py-12 text-center text-sm text-muted-foreground">Application usage appears after foreground activity is synchronized.</p>}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Daily active time</CardTitle>
            <CardDescription>Last 7 days</CardDescription>
            <CardAction><Button variant="ghost" size="sm" render={<Link href="/statistics" />}>Details <ArrowRight data-icon="inline-end" /></Button></CardAction>
          </CardHeader>
          <CardContent>
            {statistics.daily.length ? (
              <div className="flex h-56 items-end gap-3 pt-8">
                {statistics.daily.map(day => (
                  <div className="flex h-full flex-1 flex-col justify-end gap-2 text-center" key={day.date}>
                    <span className="text-[10px] text-muted-foreground tabular-nums">{formatDuration(day.activeSeconds)}</span>
                    <span className="mx-auto w-full max-w-9 rounded-t-md bg-primary/80" style={{ height: `${Math.max(3, day.activeSeconds / maxDay * 100)}%` }} />
                    <span className="text-[10px] text-muted-foreground">{new Date(`${day.date}T12:00:00`).toLocaleDateString(undefined, { weekday: "short" }).slice(0, 2)}</span>
                  </div>
                ))}
              </div>
            ) : <p className="py-12 text-center text-sm text-muted-foreground">No usage has been synchronized yet.</p>}
          </CardContent>
        </Card>
      </section>
    </>
  );
}

function Heading() {
  return <PageHeader eyebrow="Overview" title="Today" description="Screen time, app usage, and device status at a glance." />;
}
