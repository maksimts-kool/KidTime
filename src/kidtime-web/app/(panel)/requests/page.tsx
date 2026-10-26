import { Clock } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import type { ApplicationSummary, DeviceSummary, ExtraTimeTarget, TimeExtensionRequest } from "@/lib/types";
import { AutoRefresh } from "@/components/auto-refresh";
import { GiveExtraTime } from "@/components/give-extra-time";
import { PageHeader } from "@/components/page-header";
import { TimeExtensionRequests } from "@/components/time-extension-requests";
import { Card, CardContent } from "@/components/ui/card";

export default async function RequestsPage() {
  const [requests, devices, applications] = await Promise.all([
    backendFetch<TimeExtensionRequest[]>("/api/time-extensions?includeDecided=true"),
    backendFetch<DeviceSummary[]>("/api/devices"),
    backendFetch<ApplicationSummary[]>("/api/applications"),
  ]);
  const pending = requests.filter(request => request.status === "Pending");
  // Each PC, then the applications on it that carry a rule - extra time means nothing to an
  // application with no limit, block or schedule to lift.
  const multipleDevices = devices.length > 1;
  const targets: ExtraTimeTarget[] = devices.flatMap(device => [
    { deviceId: device.id, deviceApplicationId: null, label: multipleDevices ? `${device.name} · PC screen time` : "PC screen time" },
    ...applications
      .filter(application => application.deviceId === device.id
        && (application.manuallyBlocked || application.dailyLimitSeconds != null || application.scheduleConfigured))
      .map(application => ({
        deviceId: device.id,
        deviceApplicationId: application.id,
        label: multipleDevices ? `${device.name} · ${application.displayName}` : application.displayName,
      })),
  ]);

  return (
    <>
      <AutoRefresh />
      <PageHeader
        eyebrow="Extra time"
        title="Requests"
        description="When a PC or an app has five minutes or less left, the child can ask for more time. Approving grants the minutes for that day only; they reach the PC with the next rule sync and expire on their own at midnight. You can also give time without being asked."
        actions={<GiveExtraTime targets={targets} variant="default" />}
      />
      {requests.length === 0 ? (
        <Card>
          <CardContent className="flex min-h-64 flex-col items-center justify-center px-6 text-center">
            <span className="mb-4 flex size-12 items-center justify-center rounded-full bg-primary/10 text-primary"><Clock className="size-5" /></span>
            <h2 className="font-medium">No requests yet</h2>
            <p className="mt-1 max-w-md text-sm text-muted-foreground">A request appears here as soon as a child asks for extra time on an enrolled PC.</p>
          </CardContent>
        </Card>
      ) : (
        <>
          {pending.length === 0 && (
            <Card className="mb-6">
              <CardContent className="py-6 text-center text-sm text-muted-foreground">Nothing waiting on you right now.</CardContent>
            </Card>
          )}
          <TimeExtensionRequests requests={requests} />
        </>
      )}
    </>
  );
}
