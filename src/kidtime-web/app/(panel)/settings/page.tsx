import Link from "next/link";
import {
  AppWindow,
  ArrowRight,
  CheckCircle2,
  CircleSlash2,
  Info,
  Languages,
  MonitorCog,
  PackageCheck,
  ShieldAlert,
  Sparkles,
  TriangleAlert,
} from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration } from "@/lib/format";
import { buildSuggestions, type Suggestion } from "@/lib/suggestions";
import type { ApplicationSummary, DeviceStatistics, DeviceSummary, TimeExtensionRequest } from "@/lib/types";
import { PageHeader } from "@/components/page-header";
import { StatCard } from "@/components/stat-card";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { cn } from "@/lib/utils";

const toneStyles = {
  critical: { chip: "bg-destructive/10 text-destructive", icon: ShieldAlert },
  warning: { chip: "bg-amber-500/10 text-amber-600", icon: TriangleAlert },
  info: { chip: "bg-primary/10 text-primary", icon: Info },
} as const;

export default async function SettingsPage() {
  const [devices, applications, requests] = await Promise.all([
    backendFetch<DeviceSummary[]>("/api/devices"),
    backendFetch<ApplicationSummary[]>("/api/applications"),
    backendFetch<TimeExtensionRequest[]>("/api/time-extensions"),
  ]);
  const statistics = new Map(
    await Promise.all(
      devices.map(async device =>
        [device.id, await backendFetch<DeviceStatistics>(`/api/statistics/devices/${device.id}?days=7`)] as const),
    ),
  );
  const suggestions = buildSuggestions({
    devices,
    applications,
    statistics,
    unresolvedFaults: devices.reduce((sum, device) => sum + device.unresolvedFaults, 0),
    pendingRequests: requests.length,
  });
  const weekly = [...statistics.values()].reduce((sum, item) => sum + item.totalActiveSeconds, 0);
  const languages = new Set(devices.map(device => device.language));

  return (
    <>
      <PageHeader
        eyebrow="Administration"
        title="Settings"
        description="What this installation looks like right now, what it would be better for, and what it does and does not collect."
      />

      <section className="mb-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard label="Enrolled PCs" value={devices.length.toString()} icon={MonitorCog} detail={`${devices.filter(device => device.isOnline).length} reporting now`} />
        <StatCard label="Applications known" value={applications.length.toString()} icon={AppWindow} detail={`${applications.filter(item => item.manuallyBlocked || item.dailyLimitSeconds != null || item.scheduleConfigured).length} carry a rule`} />
        <StatCard label="Screen time this week" value={formatDuration(weekly)} icon={Sparkles} detail="Across every enrolled PC" />
        <StatCard
          label="Agent language"
          value={languages.size === 1 ? [...languages][0] : `${languages.size} in use`}
          icon={Languages}
          detail="What the child's PC speaks"
        />
      </section>

      <Card className="mb-4">
        <CardHeader className={suggestions.length ? "border-b" : undefined}>
          <CardTitle>Suggestions</CardTitle>
          <CardDescription>Worked out from what this installation already knows. Nothing here changes anything by itself.</CardDescription>
        </CardHeader>
        <CardContent className={suggestions.length ? "px-0" : undefined}>
          {suggestions.length ? (
            <ul>
              {suggestions.map(suggestion => <SuggestionRow key={suggestion.id} suggestion={suggestion} />)}
            </ul>
          ) : (
            <div className="flex flex-col items-center py-12 text-center">
              <span className="mb-3 flex size-10 items-center justify-center rounded-full bg-primary/10 text-primary"><PackageCheck className="size-5" /></span>
              <h3 className="font-medium">Nothing to suggest</h3>
              <p className="mt-1 max-w-md text-sm text-muted-foreground">
                Every PC has a controlled account and a rule, no faults are waiting, and no child is waiting on an answer.
              </p>
            </div>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Privacy scope</CardTitle><CardDescription>KidTime collects only what is required for local screen-time enforcement.</CardDescription></CardHeader>
        <CardContent className="grid gap-4 md:grid-cols-2">
          <div className="rounded-lg border bg-muted/40 p-4"><CheckCircle2 className="mb-3 size-5 text-primary" /><h3 className="text-sm font-medium">Included</h3><p className="mt-1 text-sm leading-relaxed text-muted-foreground">Active PC time, foreground application identity, idle state, device health, and local enforcement events.</p></div>
          <div className="rounded-lg border bg-muted/40 p-4"><CircleSlash2 className="mb-3 size-5 text-muted-foreground" /><h3 className="text-sm font-medium">Never collected</h3><p className="mt-1 text-sm leading-relaxed text-muted-foreground">Websites, searches, keystrokes, screenshots, messages, camera, microphone, or network traffic.</p></div>
        </CardContent>
      </Card>
    </>
  );
}

function SuggestionRow({ suggestion }: { suggestion: Suggestion }) {
  const { chip, icon: Icon } = toneStyles[suggestion.tone];
  return (
    <li className="border-b last:border-b-0">
      <div className="flex flex-wrap items-start gap-3 px-(--card-spacing) py-4">
        <span className={cn("flex size-9 shrink-0 items-center justify-center rounded-lg", chip)}>
          <Icon className="size-4" aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <p className="font-medium">{suggestion.title}</p>
          <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{suggestion.detail}</p>
        </div>
        <Button variant="outline" size="sm" render={<Link href={suggestion.href} />}>
          {suggestion.action} <ArrowRight data-icon="inline-end" />
        </Button>
      </div>
    </li>
  );
}
