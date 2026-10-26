import Link from "next/link";
import { ArrowLeft, CloudDownload, CloudOff, MonitorCheck, RefreshCw, TriangleAlert, UserRound, UserRoundX, Wifi } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatSeen } from "@/lib/format";
import { describeTodayWindows } from "@/lib/schedule";
import { deviceNowState } from "@/lib/status";
import type { DeviceDetail, DeviceSummary, SpecialPeriod } from "@/lib/types";
import { AutoRefresh } from "@/components/auto-refresh";
import { DeviceRuleEditor } from "@/components/device-rule-editor";
import { GiveExtraTime } from "@/components/give-extra-time";
import { PageHeader } from "@/components/page-header";
import { QuickBlock } from "@/components/quick-block";
import { RemoveDevice } from "@/components/remove-device";
import { ScheduleCard } from "@/components/schedule-card";
import { ScheduleStrip } from "@/components/schedule-strip";
import { ActionRow, NowCard, PageSection, StateBadge, StatusTile, type StatusTone } from "@/components/status-panel";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";

export default async function DevicePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const [detail, periods, devices] = await Promise.all([
    backendFetch<DeviceDetail>(`/api/devices/${id}`),
    backendFetch<SpecialPeriod[]>(`/api/special-periods?deviceId=${id}`),
    // The list carries what today looks like - usage, the schedule's state, faults waiting - and
    // the device's own endpoint carries its rules; the page needs both.
    backendFetch<DeviceSummary[]>("/api/devices"),
  ]);
  const summary = devices.find(device => device.id === id) ?? null;
  const { device, rules } = detail;
  const online = device.isOnline;
  const synced = device.appliedRuleRevision >= rules.revision;

  const update: { value: string; hint: string; tone: StatusTone } = !device.agentVersion
    ? { value: "Not reported yet", hint: "Appears after the PC's first sync.", tone: "idle" }
    : device.isAgentUpToDate
      ? { value: `v${device.agentVersion}`, hint: "Up to date.", tone: "ok" }
      : device.agentUpdateStatus === "Failed"
        ? { value: `v${device.agentVersion}`, hint: device.agentUpdateError ?? "The last update failed and was rolled back.", tone: "bad" }
        : device.agentUpdateStatus === "Downloading" || device.agentUpdateStatus === "Installing"
          ? { value: `Updating to v${device.latestAgentVersion}`, hint: `${device.agentUpdateStatus} now.`, tone: "warn" }
          : device.latestAgentVersion
            ? { value: `v${device.agentVersion}`, hint: `v${device.latestAgentVersion} is published; it installs on its own within minutes.`, tone: "warn" }
            : { value: `v${device.agentVersion}`, hint: "No release is published on this server.", tone: "idle" };
  const faults = summary?.unresolvedFaults ?? 0;

  return (
    <>
      <AutoRefresh seconds={30} />
      <Button variant="ghost" size="sm" className="mb-4 -ml-2" nativeButton={false} render={<Link href="/devices" />}><ArrowLeft data-icon="inline-start" />Devices</Button>
      <PageHeader
        eyebrow="Device"
        title={device.name}
        description={`${device.windowsVersion} · ${device.timeZoneId}`}
        actions={(
          <>
            <GiveExtraTime size="xl" targets={[{ deviceId: id, deviceApplicationId: null, label: "PC screen time" }]} />
            <QuickBlock size="xl" deviceId={id} blocked={rules.manuallyBlocked} />
          </>
        )}
      />

      <PageSection label="Status">
        {summary && (
          <NowCard
            state={deviceNowState(summary)}
            usedSeconds={summary.todayActiveSeconds}
            limitSeconds={summary.dailyLimitSeconds}
            badges={(
              <>
                {summary.schedule.specialPeriod && <StateBadge tone="ok">{summary.schedule.specialPeriod.name} in force</StateBadge>}
                {online && summary.foregroundApplication && <StateBadge>Using {summary.foregroundApplication}</StateBadge>}
              </>
            )}
          >
            {summary.schedule.configured && (
              <div>
                <p className="mb-1 text-xs text-muted-foreground tabular-nums">{describeTodayWindows(summary)}</p>
                <ScheduleStrip windows={summary.schedule.todayWindows} nowMinute={summary.schedule.nowMinuteOfDay} />
              </div>
            )}
          </NowCard>
        )}
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
          <StatusTile
            label="Connection"
            icon={online ? Wifi : CloudOff}
            tone={online ? "ok" : "warn"}
            value={online ? "Online" : "Offline"}
            hint={online
              ? device.loggedInUser ? `Signed in as ${device.loggedInUser}.` : "Nobody is signed in."
              : `Last seen ${formatSeen(device.lastSeenUtc).toLowerCase()}. Cached rules keep applying.`}
          />
          <StatusTile
            label="Rules on the PC"
            icon={RefreshCw}
            tone={synced ? "ok" : "warn"}
            value={synced ? "Up to date" : online ? "Syncing" : "Waiting for the PC"}
            hint={synced ? `Revision ${rules.revision} applied.` : online ? `Revision ${rules.revision} is on its way.` : "Your latest changes apply when it reconnects."}
          />
          <StatusTile
            label="Child's account"
            icon={rules.controlledUserName ? UserRound : UserRoundX}
            tone={rules.controlledUserName ? "ok" : "bad"}
            value={rules.controlledUserName ?? "Not chosen"}
            hint={rules.controlledUserName ? "Rules apply only while it is signed in." : "Nothing is enforced. Choose it below."}
            href={rules.controlledUserName ? undefined : "#account"}
          />
          <StatusTile label="KidTime on the PC" icon={CloudDownload} tone={update.tone} value={update.value} hint={update.hint} />
          <StatusTile
            label="Errors"
            icon={faults ? TriangleAlert : MonitorCheck}
            tone={faults ? "bad" : "ok"}
            value={faults ? `${faults} waiting` : "None"}
            hint={faults ? "Open the error log to see what went wrong." : "Nothing has gone wrong on this PC."}
            href={faults ? "/diagnostics" : undefined}
          />
        </div>
      </PageSection>

      <PageSection label="Settings">
        <ScheduleCard
          scheduleEndpoint={`/api/backend/devices/${id}/schedule`}
          limitEndpoint={`/api/backend/devices/${id}/limit`}
          schedule={rules.schedule}
          periods={periods}
          deviceId={id}
          dailyLimitSeconds={rules.dailyLimitSeconds}
          scope="the PC"
          description="How long and when the PC may be used: every week, and differently on holidays or other dates you set."
        />

        <DeviceRuleEditor deviceId={id} rule={rules} accounts={detail.availableWindowsUsers} />

        <Card className="ring-destructive/30">
          <CardContent>
            <ActionRow
              title="Remove this PC"
              description="Deletes its rules, history and credentials from the server. KidTime stays installed on the PC until it is removed there."
            >
              <RemoveDevice deviceId={id} deviceName={device.name} />
            </ActionRow>
          </CardContent>
        </Card>
      </PageSection>
    </>
  );
}
