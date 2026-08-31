import Link from "next/link";
import { ArrowRight, Clock3, HandHelping, Monitor, Sparkles } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration, formatSeen } from "@/lib/format";
import { describeApplicationRules, describeSchedule, describeTodayWindows } from "@/lib/schedule";
import type { ApplicationSummary, DeviceStatistics, DeviceSummary, TimeExtensionRequest } from "@/lib/types";
import { ApplicationIcon } from "@/components/application-icon";
import { AutoRefresh } from "@/components/auto-refresh";
import { EmptyDevices } from "@/components/empty-state";
import { PageHeader } from "@/components/page-header";
import { QuickBlock } from "@/components/quick-block";
import { ScheduleStrip } from "@/components/schedule-strip";
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
  const [statistics, applications, requests] = await Promise.all([
    backendFetch<DeviceStatistics>(`/api/statistics/devices/${device.id}`),
    backendFetch<ApplicationSummary[]>(`/api/applications?deviceId=${device.id}`),
    backendFetch<TimeExtensionRequest[]>("/api/time-extensions"),
  ]);
  const percent = device.dailyLimitSeconds
    ? Math.min(100, device.todayActiveSeconds / device.dailyLimitSeconds * 100)
    : 0;
  // The weekly schedule is the rule most households actually govern the PC with, so it gets the
  // headline the daily limit used to hold twice over.
  const schedule = describeSchedule(device);
  const todayWindows = describeTodayWindows(device);
  const top = [...applications].sort((a, b) => b.todayActiveSeconds - a.todayActiveSeconds).slice(0, 5);
  const maxDay = Math.max(...statistics.daily.map(day => day.activeSeconds), 1);

  return (
    <>
      <AutoRefresh seconds={30} />
      <Heading />
      {requests.length > 0 && (
        /*
          A child asks for more time with minutes left on the clock, so the request has to be
          visible on the page a parent already has open, not only on one they might navigate to.
        */
        <Card className="mb-4 border-primary/40 bg-primary/5">
          <CardContent className="flex flex-wrap items-center justify-between gap-3 py-4">
            <div className="flex items-center gap-3">
              <span className="flex size-9 items-center justify-center rounded-lg bg-primary/10 text-primary"><HandHelping className="size-4" /></span>
              <div>
                <p className="font-medium">{requests.length === 1 ? "A request is waiting" : `${requests.length} requests are waiting`}</p>
                <p className="text-sm text-muted-foreground">
                  {requests[0].deviceName} asked for {requests[0].requestedMinutes} more minutes on{" "}
                  {requests[0].isPc ? "PC screen time" : requests[0].displayName}
                  {requests.length > 1 ? ", and more" : ""}.
                </p>
              </div>
            </div>
            <Button render={<Link href="/requests" />}>Review <ArrowRight data-icon="inline-end" /></Button>
          </CardContent>
        </Card>
      )}
      <Card className="mb-4">
        <CardHeader className="border-b">
          <div className="flex items-center gap-2"><StatusBadge online={device.isOnline} /><span className="text-sm text-muted-foreground">{device.windowsVersion}</span></div>
          <CardTitle className="text-xl">{device.name}</CardTitle>
          {/* An offline PC reports nothing, so its last foreground application is a stale fact rather than a current one. */}
          <CardDescription>{!device.isOnline ? `Last seen ${formatSeen(device.lastSeenUtc)}` : device.foregroundApplication ? <>Currently using <span className="font-medium text-foreground">{device.foregroundApplication}</span></> : "No active application reported"}</CardDescription>
          <CardAction><QuickBlock deviceId={device.id} blocked={device.manuallyBlocked} /></CardAction>
        </CardHeader>
        <CardContent className="grid gap-5 pt-1 md:grid-cols-[1fr_auto] md:items-end">
          <div>
            <p className="text-xs text-muted-foreground">Screen time today</p>
            <p className="mt-1 text-2xl font-semibold tabular-nums">{formatDuration(device.todayActiveSeconds)}</p>
            {device.dailyLimitSeconds ? (
              <>
                <div className="mt-3 mb-2 flex justify-between text-xs text-muted-foreground tabular-nums">
                  <span>Daily limit {formatDuration(device.dailyLimitSeconds)}</span>
                  <span>{formatDuration(device.remainingSeconds)} left</span>
                </div>
                <Progress value={percent} aria-label={`${Math.round(percent)} percent of today's PC limit used`} />
              </>
            ) : <p className="mt-2 text-xs text-muted-foreground">No daily limit</p>}
          </div>
          <div className="min-w-44 rounded-xl border bg-muted/50 px-4 py-3 md:text-right">
            <p className="text-xs text-muted-foreground">{schedule.label}</p>
            <p className="mt-1 text-xl font-semibold tabular-nums">{schedule.value}</p>
          </div>
          {device.schedule.configured && (
            <div className="md:col-span-2">
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
                    <p className="truncate text-xs text-muted-foreground">{describeApplicationRules(app) ?? app.publisher ?? app.executableName}</p>
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
