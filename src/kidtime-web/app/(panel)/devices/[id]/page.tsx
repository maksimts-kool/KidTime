import Link from "next/link";
import { ArrowLeft, CircleCheck, CloudDownload, CloudOff, RefreshCw, UserRound } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatSeen } from "@/lib/format";
import type { DeviceDetail } from "@/lib/types";
import { DeviceRuleEditor } from "@/components/device-rule-editor";
import { PageHeader } from "@/components/page-header";
import { QuickBlock } from "@/components/quick-block";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";

export default async function DevicePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const detail = await backendFetch<DeviceDetail>(`/api/devices/${id}`);
  const online = detail.device.isOnline;
  const synced = detail.device.appliedRuleRevision >= detail.rules.revision;
  const updateValue = detail.device.isAgentUpToDate
    ? `v${detail.device.agentVersion} is current`
    : detail.device.agentUpdateStatus === "Installing" || detail.device.agentUpdateStatus === "Downloading"
      ? `${detail.device.agentUpdateStatus} v${detail.device.latestAgentVersion ?? "latest"}`
      : `v${detail.device.agentVersion ?? "unknown"} · latest v${detail.device.latestAgentVersion ?? "unpublished"}`;
  const facts = [
    { label: "Connection", value: online ? "Online" : `Last seen ${formatSeen(detail.device.lastSeenUtc)}`, icon: online ? CircleCheck : CloudOff, good: online },
    { label: "Rule sync", value: synced ? `Revision ${detail.rules.revision} applied` : `Waiting for revision ${detail.rules.revision}`, icon: RefreshCw, good: synced },
    { label: "Logged-in user", value: detail.device.loggedInUser ?? "Not reported", icon: UserRound, good: Boolean(detail.device.loggedInUser) },
    { label: "KidTime services", value: updateValue, icon: CloudDownload, good: detail.device.isAgentUpToDate },
  ];
  return (
    <>
      <Button variant="ghost" size="sm" className="mb-4 -ml-2" render={<Link href="/devices" />}><ArrowLeft data-icon="inline-start" />Devices</Button>
      <PageHeader
        eyebrow="Device settings"
        title={detail.device.name}
        description={`${detail.device.windowsVersion} · ${detail.device.timeZoneId}`}
        actions={<QuickBlock deviceId={id} blocked={detail.rules.manuallyBlocked} />}
      />
      <Card className="mb-4">
        <CardContent className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          {facts.map(({ label, value, icon: Icon, good }) => (
            <div className="flex items-center gap-3 rounded-lg bg-muted/60 p-3" key={label}>
              <span className={good ? "flex size-9 items-center justify-center rounded-lg bg-primary/10 text-primary" : "flex size-9 items-center justify-center rounded-lg bg-background text-muted-foreground"}><Icon className="size-4" /></span>
              <div className="min-w-0"><p className="text-xs text-muted-foreground">{label}</p><p className="truncate text-sm font-medium">{value}</p></div>
            </div>
          ))}
        </CardContent>
      </Card>
      <DeviceRuleEditor deviceId={id} rule={detail.rules} accounts={detail.availableWindowsUsers} />
    </>
  );
}
