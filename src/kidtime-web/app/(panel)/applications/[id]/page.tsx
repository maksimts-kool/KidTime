import Link from "next/link";
import { ArrowLeft, BadgeCheck, CalendarRange, Clock3, FileText, Flame, FolderOpen, Gauge } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration } from "@/lib/format";
import { busiestDay, changeAgainst, formatDayLabel, formatWeekday } from "@/lib/chart";
import type { DailyUsage, WeeklySchedule } from "@/lib/types";
import { ApplicationIcon } from "@/components/application-icon";
import { ApplicationRuleEditor } from "@/components/application-rule-editor";
import { DailyUsageChart } from "@/components/charts/daily-usage-chart";
import { StatCard } from "@/components/stat-card";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";

type Detail = {
  id: string;
  device: { id: string; name: string };
  application: { displayName: string; executableName: string; productName: string | null; company: string | null; signaturePublisher: string | null; originalFilename: string | null; packageFamilyName: string | null };
  executablePath: string; fileVersion: string | null; sha256: string | null; firstSeenUtc: string; lastSeenUtc: string; hasIcon: boolean;
  rule: { manuallyBlocked: boolean; dailyLimitSeconds: number | null; schedule: WeeklySchedule };
  usage: DailyUsage[];
};

type ApplicationStatistics = {
  from: string;
  to: string;
  days: number;
  totalActiveSeconds: number;
  previousTotalActiveSeconds: number;
  dailyAverageSeconds: number;
  daily: DailyUsage[];
};

const days = 14;

export default async function ApplicationPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const [detail, statistics] = await Promise.all([
    backendFetch<Detail>(`/api/applications/${id}`),
    backendFetch<ApplicationStatistics>(`/api/statistics/applications/${id}?days=${days}`),
  ]);
  // The last day of the range is the PC's own today, which is the one the child is still adding to.
  const today = statistics.to;
  const todaySeconds = statistics.daily.find(item => item.date === today)?.activeSeconds ?? 0;
  const busiest = busiestDay(statistics.daily);
  const trend = changeAgainst(statistics.totalActiveSeconds, statistics.previousTotalActiveSeconds);
  const metadata = [
    { label: "Executable path", value: detail.executablePath, icon: FolderOpen },
    { label: "Signed publisher", value: detail.application.signaturePublisher ?? "Not available", icon: BadgeCheck },
    { label: "Version / original file", value: `${detail.fileVersion ?? "Unknown"} · ${detail.application.originalFilename ?? detail.application.executableName}`, icon: FileText },
  ];
  return (
    <>
      <Button variant="ghost" size="sm" className="mb-4 -ml-2" render={<Link href="/applications" />}><ArrowLeft data-icon="inline-start" />Applications</Button>
      <div className="mb-6 flex items-center gap-4">
        <ApplicationIcon applicationId={id} displayName={detail.application.displayName} hasIcon={detail.hasIcon} size="large" />
        <div className="min-w-0"><p className="mb-1 text-xs font-medium tracking-wide text-primary uppercase">{detail.device.name}</p><h1 className="truncate text-2xl font-semibold tracking-tight sm:text-3xl">{detail.application.displayName}</h1><p className="mt-1 truncate text-sm text-muted-foreground">{detail.application.signaturePublisher ?? detail.application.company ?? detail.application.executableName}</p></div>
      </div>
      <section className="mb-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard
          label="Today"
          value={formatDuration(todaySeconds)}
          icon={Clock3}
          detail={detail.rule.dailyLimitSeconds ? `${formatDuration(Math.max(0, detail.rule.dailyLimitSeconds - todaySeconds))} remaining` : "No daily limit"}
        />
        <StatCard
          label={`Last ${days} days`}
          value={formatDuration(statistics.totalActiveSeconds)}
          icon={CalendarRange}
          trend={trend}
          trendLabel={trend == null ? "Foreground active time" : `vs the ${days} days before`}
        />
        <StatCard label="Daily average" value={formatDuration(statistics.dailyAverageSeconds)} icon={Gauge} detail={`Across ${statistics.days} days`} />
        <StatCard
          label="Busiest day"
          value={busiest ? formatDuration(busiest.activeSeconds) : "—"}
          icon={Flame}
          detail={busiest ? `${formatWeekday(busiest.date)}, ${formatDayLabel(busiest.date)}` : "Nothing recorded yet"}
        />
      </section>
      <Card className="mb-4">
        <CardHeader>
          <CardTitle>Daily active time</CardTitle>
          <CardDescription>
            {formatDayLabel(statistics.from)} — {formatDayLabel(statistics.to)} · the dashed line is the average
            {detail.rule.dailyLimitSeconds ? ", the red one this application's daily limit" : ""}
          </CardDescription>
        </CardHeader>
        <CardContent>
          {statistics.totalActiveSeconds > 0 ? (
            <DailyUsageChart daily={statistics.daily} limitSeconds={detail.rule.dailyLimitSeconds} today={today} height={240} />
          ) : (
            <p className="py-14 text-center text-sm text-muted-foreground">No foreground time has been recorded for these days.</p>
          )}
        </CardContent>
      </Card>
      <Card className="mb-4">
        <CardHeader><CardTitle>Application details</CardTitle><CardDescription>Identity information reported by Windows.</CardDescription></CardHeader>
        <CardContent>
          {metadata.map(({ label, value, icon: Icon }, index) => (
            <div key={label}>{index > 0 && <Separator />}<div className="flex gap-3 py-3"><Icon className="mt-0.5 size-4 shrink-0 text-muted-foreground" /><div className="min-w-0"><p className="text-xs text-muted-foreground">{label}</p><p className="mt-0.5 break-all text-sm font-medium">{value}</p></div></div></div>
          ))}
        </CardContent>
      </Card>
      <ApplicationRuleEditor applicationId={id} blocked={detail.rule.manuallyBlocked} dailyLimitSeconds={detail.rule.dailyLimitSeconds} schedule={detail.rule.schedule} />
    </>
  );
}
