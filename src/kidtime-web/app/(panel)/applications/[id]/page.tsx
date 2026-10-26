import Link from "next/link";
import { ArrowLeft, BadgeCheck, FileText, FolderOpen } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDayLabel } from "@/lib/chart";
import type { ApplicationSummary, DailyUsage, SpecialPeriod, WeeklySchedule } from "@/lib/types";
import { applicationNowState } from "@/lib/status";
import { ApplicationIcon } from "@/components/application-icon";
import { AutoRefresh } from "@/components/auto-refresh";
import { ApplicationBlock } from "@/components/application-block";
import { DailyUsageChart } from "@/components/charts/daily-usage-chart";
import { GiveExtraTime } from "@/components/give-extra-time";
import { ScheduleCard } from "@/components/schedule-card";
import { NowCard, PageSection, StateBadge } from "@/components/status-panel";
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
  const [periods, applications] = await Promise.all([
    backendFetch<SpecialPeriod[]>(`/api/special-periods?deviceId=${detail.device.id}&deviceApplicationId=${id}`),
    // The list is what knows whether the schedule is open right now and which limit is in force
    // today, a special period's included.
    backendFetch<ApplicationSummary[]>(`/api/applications?deviceId=${detail.device.id}`),
  ]);
  const summary = applications.find(application => application.id === id);
  // The last day of the range is the PC's own today, which is the one the child is still adding to.
  const today = statistics.to;
  const todaySeconds = statistics.daily.find(item => item.date === today)?.activeSeconds ?? 0;
  const metadata = [
    { label: "Executable path", value: detail.executablePath, icon: FolderOpen },
    { label: "Signed publisher", value: detail.application.signaturePublisher ?? "Not available", icon: BadgeCheck },
    { label: "Version / original file", value: `${detail.fileVersion ?? "Unknown"} · ${detail.application.originalFilename ?? detail.application.executableName}`, icon: FileText },
  ];
  const limitToday = summary ? summary.dailyLimitSeconds : detail.rule.dailyLimitSeconds;
  const now = applicationNowState({
    manuallyBlocked: detail.rule.manuallyBlocked,
    dailyLimitSeconds: limitToday,
    todayActiveSeconds: todaySeconds,
    scheduleConfigured: summary?.scheduleConfigured ?? detail.rule.schedule.days.length > 0,
    withinSchedule: summary?.withinSchedule ?? true,
  });
  const activePeriod = periods.find(period => period.isActive);
  return (
    <>
      <AutoRefresh seconds={30} />
      <Button variant="ghost" size="sm" className="mb-4 -ml-2" nativeButton={false} render={<Link href="/applications" />}><ArrowLeft data-icon="inline-start" />Applications</Button>
      <div className="mb-6 flex items-center gap-4">
        <ApplicationIcon applicationId={id} displayName={detail.application.displayName} hasIcon={detail.hasIcon} size="large" />
        <div className="min-w-0 flex-1"><p className="mb-1 text-xs font-medium tracking-wide text-primary uppercase">{detail.device.name}</p><h1 className="truncate text-2xl font-semibold tracking-tight sm:text-3xl">{detail.application.displayName}</h1><p className="mt-1 truncate text-sm text-muted-foreground">{detail.application.signaturePublisher ?? detail.application.company ?? detail.application.executableName}</p></div>
      </div>

      <PageSection title="Status">
        <NowCard
          state={now}
          usedSeconds={todaySeconds}
          limitSeconds={limitToday}
          badges={activePeriod && <StateBadge tone="ok">{activePeriod.name} in force</StateBadge>}
        />
      </PageSection>

      <PageSection title="History">
        <Card>
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
        <Card>
          <CardHeader><CardTitle>Application details</CardTitle><CardDescription>Identity information reported by Windows.</CardDescription></CardHeader>
          <CardContent>
            {metadata.map(({ label, value, icon: Icon }, index) => (
              <div key={label}>{index > 0 && <Separator />}<div className="flex gap-3 py-3"><Icon className="mt-0.5 size-4 shrink-0 text-muted-foreground" /><div className="min-w-0"><p className="text-xs text-muted-foreground">{label}</p><p className="mt-0.5 break-all text-sm font-medium">{value}</p></div></div></div>
            ))}
          </CardContent>
        </Card>
      </PageSection>

      <PageSection title="Settings">
        <Card>
          <CardHeader><CardTitle>Quick actions</CardTitle></CardHeader>
          <CardContent className="flex flex-wrap gap-2">
            <GiveExtraTime targets={[{ deviceId: detail.device.id, deviceApplicationId: id, label: detail.application.displayName }]} />
            <ApplicationBlock applicationId={id} displayName={detail.application.displayName} blocked={detail.rule.manuallyBlocked} />
          </CardContent>
        </Card>
        <ScheduleCard
          scheduleEndpoint={`/api/backend/applications/${id}/schedule`}
          limitEndpoint={`/api/backend/applications/${id}/limit`}
          schedule={detail.rule.schedule}
          periods={periods}
          deviceId={detail.device.id}
          deviceApplicationId={id}
          dailyLimitSeconds={detail.rule.dailyLimitSeconds}
          scope={detail.application.displayName}
          description="How long and when the application may be used. It closes when its time or its current range runs out."
        />
      </PageSection>
    </>
  );
}
