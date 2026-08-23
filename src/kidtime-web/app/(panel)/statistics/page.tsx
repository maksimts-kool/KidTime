import { backendFetch } from "@/lib/backend";
import { formatDuration } from "@/lib/format";
import type { DeviceStatistics, DeviceSummary } from "@/lib/types";
import { PageHeader } from "@/components/page-header";
import { StatCard } from "@/components/stat-card";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export default async function StatisticsPage({ searchParams }: { searchParams: Promise<{ device?: string }> }) {
  const devices = await backendFetch<DeviceSummary[]>("/api/devices");
  const requested = (await searchParams).device;
  const device = devices.find(item => item.id === requested) ?? devices[0];
  const statistics = device ? await backendFetch<DeviceStatistics>(`/api/statistics/devices/${device.id}`) : null;
  const maxDay = Math.max(...(statistics?.daily.map(day => day.activeSeconds) ?? [1]), 1);
  const maxApp = Math.max(...(statistics?.applications.map(app => app.activeSeconds) ?? [1]), 1);
  const selector = devices.length > 1 ? (
    <form className="flex items-center gap-2">
      <select className="h-8 rounded-lg border bg-background px-2 text-sm" name="device" defaultValue={device?.id}>{devices.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select>
      <Button variant="outline">View</Button>
    </form>
  ) : undefined;
  return (
    <>
      <PageHeader eyebrow="Active time only" title="Statistics" description="Foreground use with idle periods removed." actions={selector} />
      {!statistics ? (
        <Card><CardContent className="py-20 text-center"><h2 className="font-medium">No device statistics yet</h2><p className="mt-1 text-sm text-muted-foreground">Enroll a device to begin collecting active-time totals.</p></CardContent></Card>
      ) : (
        <>
          <section className="mb-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
            <StatCard label="Total active time" value={formatDuration(statistics.totalActiveSeconds)} detail={`${statistics.from} — ${statistics.to}`} />
            <StatCard label="Daily average" value={formatDuration(Math.round(statistics.totalActiveSeconds / 7))} detail="Last 7 days" />
            <StatCard label="Top application" value={statistics.applications[0]?.displayName ?? "—"} detail={formatDuration(statistics.applications[0]?.activeSeconds ?? 0)} />
          </section>
          <section className="grid gap-4 xl:grid-cols-[1.2fr_.8fr]">
            <Card>
              <CardHeader><CardTitle>Daily usage</CardTitle><CardDescription>Active PC time by day</CardDescription></CardHeader>
              <CardContent>
                {statistics.daily.length ? <div className="flex h-72 items-end gap-3 pt-8">{statistics.daily.map(day => (
                  <div className="flex h-full flex-1 flex-col justify-end gap-2 text-center" key={day.date}><span className="text-[10px] text-muted-foreground tabular-nums">{formatDuration(day.activeSeconds)}</span><span className="mx-auto w-full max-w-12 rounded-t-md bg-primary/80" style={{ height: `${Math.max(3, day.activeSeconds / maxDay * 100)}%` }} /><small className="text-[10px] text-muted-foreground">{new Date(`${day.date}T12:00:00`).toLocaleDateString(undefined, { weekday: "short" })}</small></div>
                ))}</div> : <p className="py-12 text-center text-sm text-muted-foreground">No daily totals received yet.</p>}
              </CardContent>
            </Card>
            <Card>
              <CardHeader><CardTitle>Most used applications</CardTitle><CardDescription>Last 7 days</CardDescription></CardHeader>
              <CardContent className="grid gap-4">
                {statistics.applications.length ? statistics.applications.slice(0, 8).map(app => (
                  <div key={app.identityKey}><div className="mb-1.5 flex items-center justify-between gap-3 text-sm"><span className="truncate font-medium">{app.displayName}</span><span className="shrink-0 text-xs text-muted-foreground tabular-nums">{formatDuration(app.activeSeconds)}</span></div><div className="h-1.5 overflow-hidden rounded-full bg-muted"><div className="h-full rounded-full bg-primary" style={{ width: `${app.activeSeconds / maxApp * 100}%` }} /></div></div>
                )) : <p className="py-12 text-center text-sm text-muted-foreground">No application usage received yet.</p>}
              </CardContent>
            </Card>
          </section>
        </>
      )}
    </>
  );
}
