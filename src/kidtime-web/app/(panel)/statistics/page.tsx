import Link from "next/link";
import type { LucideIcon } from "lucide-react";
import { AppWindow, BarChart3, Clock3, Flame, Gauge, Layers } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration } from "@/lib/format";
import { busiestDay, changeAgainst, formatDayLabel, formatWeekday } from "@/lib/chart";
import type { DeviceStatistics, DeviceSummary } from "@/lib/types";
import { ApplicationUsageChart } from "@/components/charts/application-usage-chart";
import { ApplicationUsageTable } from "@/components/application-usage-table";
import { DailyUsageChart } from "@/components/charts/daily-usage-chart";
import { PageHeader } from "@/components/page-header";
import { StatCard } from "@/components/stat-card";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { cn } from "@/lib/utils";

/** The stretches a household actually thinks in. Anything longer belongs in an export, not a page. */
const ranges = [7, 14, 30] as const;

export default async function StatisticsPage({
  searchParams,
}: {
  searchParams: Promise<{ device?: string; range?: string }>;
}) {
  const devices = await backendFetch<DeviceSummary[]>("/api/devices");
  const parameters = await searchParams;
  const device = devices.find(item => item.id === parameters.device) ?? devices[0];
  const days = ranges.find(value => value === Number(parameters.range)) ?? 7;
  const statistics = device
    ? await backendFetch<DeviceStatistics>(`/api/statistics/devices/${device.id}?days=${days}`)
    : null;

  if (!device || !statistics) {
    return (
      <>
        <PageHeader eyebrow="Active time only" title="Statistics" description="Foreground use with idle periods removed." />
        <Card>
          <CardContent className="py-20 text-center">
            <h2 className="font-medium">No device statistics yet</h2>
            <p className="mt-1 text-sm text-muted-foreground">Enroll a device to begin collecting active-time totals.</p>
          </CardContent>
        </Card>
      </>
    );
  }

  const busiest = busiestDay(statistics.daily);
  const trend = changeAgainst(statistics.totalActiveSeconds, statistics.previousTotalActiveSeconds);
  const period = `${formatDayLabel(statistics.from)} — ${formatDayLabel(statistics.to)}`;
  const charted = statistics.chartedApplications;
  const named = statistics.applications.reduce((sum, item) => sum + item.activeSeconds, 0);

  return (
    <>
      {/* One filter row above everything it scopes: both charts and every number below read the same slice. */}
      <PageHeader
        eyebrow="Active time only"
        title="Statistics"
        description="Foreground use with idle periods removed, for one PC at a time."
        actions={<Filters devices={devices} deviceId={device.id} days={days} />}
      />

      <section className="mb-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard
          label="Total active time"
          value={formatDuration(statistics.totalActiveSeconds)}
          icon={Clock3}
          trend={trend}
          trendLabel={trend == null ? period : `vs the previous ${days} days`}
        />
        <StatCard
          label="Daily average"
          value={formatDuration(statistics.dailyAverageSeconds)}
          icon={Gauge}
          detail={`Across ${statistics.days} days`}
        />
        <StatCard
          label="Busiest day"
          value={busiest ? formatDuration(busiest.activeSeconds) : "—"}
          icon={Flame}
          detail={busiest ? `${formatWeekday(busiest.date)}, ${formatDayLabel(busiest.date)}` : "Nothing recorded yet"}
        />
        <StatCard
          label="Applications used"
          value={statistics.applications.length.toString()}
          icon={AppWindow}
          detail={statistics.applications[0] ? `Most used: ${statistics.applications[0].displayName}` : "Nothing recorded yet"}
        />
      </section>

      <Card className="mb-4">
        <CardHeader>
          <CardTitle><TitleIcon icon={BarChart3} />Daily active time</CardTitle>
          <CardDescription>
            {period} · the dashed line is this period&rsquo;s average
            {device.dailyLimitSeconds ? ", the red one the daily limit" : ""}
          </CardDescription>
        </CardHeader>
        <CardContent>
          {statistics.totalActiveSeconds > 0 ? (
            <DailyUsageChart
              daily={statistics.daily}
              limitSeconds={device.dailyLimitSeconds}
              today={device.schedule.localDate}
              height={280}
            />
          ) : (
            <Empty>No active time has been synchronized for these days.</Empty>
          )}
        </CardContent>
      </Card>

      <Card className="mb-4">
        <CardHeader>
          <CardTitle><TitleIcon icon={Layers} />Where the time went</CardTitle>
          <CardDescription>
            The same days split by application. The leading {charted} are named; everything else is one band.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {named > 0 ? (
            <ApplicationUsageChart
              daily={statistics.daily}
              applications={statistics.applications}
              charted={charted}
              height={280}
            />
          ) : (
            <Empty>Application usage appears after foreground activity is synchronized.</Empty>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="border-b">
          <CardTitle><TitleIcon icon={AppWindow} />Applications</CardTitle>
          <CardDescription>Share of {formatDuration(statistics.totalActiveSeconds)} of PC time, and the change against the previous {days} days.</CardDescription>
        </CardHeader>
        <CardContent>
          {statistics.applications.length ? (
            <ApplicationUsageTable
              applications={statistics.applications}
              totalActiveSeconds={statistics.totalActiveSeconds}
              charted={charted}
            />
          ) : (
            <Empty>No application usage received yet.</Empty>
          )}
        </CardContent>
      </Card>
    </>
  );
}

/** A card title reads faster with the thing it is about drawn beside it. */
function TitleIcon({ icon: Icon }: { icon: LucideIcon }) {
  return (
    <span className="mr-2 inline-flex size-6 items-center justify-center rounded-md bg-primary/10 align-[-5px] text-primary">
      <Icon className="size-3.5" aria-hidden="true" />
    </span>
  );
}

function Empty({ children }: { children: string }) {
  return <p className="py-16 text-center text-sm text-muted-foreground">{children}</p>;
}

function Filters({ devices, deviceId, days }: { devices: DeviceSummary[]; deviceId: string; days: number }) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      {devices.length > 1 && (
        <form className="flex items-center gap-2">
          <input type="hidden" name="range" value={days} />
          <select
            className="h-8 rounded-lg border bg-background px-2 text-sm"
            name="device"
            defaultValue={deviceId}
            aria-label="Device"
          >
            {devices.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
          </select>
          <Button variant="outline" size="sm">View</Button>
        </form>
      )}
      <div className="inline-flex rounded-lg border bg-muted/40 p-0.5" role="group" aria-label="Period">
        {ranges.map(value => (
          <Link
            key={value}
            href={`/statistics?device=${deviceId}&range=${value}`}
            aria-current={value === days ? "true" : undefined}
            className={cn(
              "rounded-md px-2.5 py-1 text-xs font-medium transition-colors",
              value === days ? "bg-background text-foreground shadow-xs" : "text-muted-foreground hover:text-foreground",
            )}
          >
            {value} days
          </Link>
        ))}
      </div>
    </div>
  );
}
