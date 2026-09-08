import Link from "next/link";
import { ChevronRight } from "lucide-react";
import { formatDuration } from "@/lib/format";
import { changeAgainst, seriesColor, share } from "@/lib/chart";
import type { StatisticsApplication } from "@/lib/types";
import { ApplicationIcon } from "@/components/application-icon";
import { TrendPill } from "@/components/stat-card";

/**
 * Every application in the period, in the order they took the time.
 *
 * This is also the chart's table view: the leading few carry the same colour they have in the
 * stack above, so a band and a row are visibly the same application, and the rest of the list
 * carries its numbers in writing rather than leaving colour to say them.
 */
export function ApplicationUsageTable({
  applications,
  totalActiveSeconds,
  charted = 6,
}: {
  applications: StatisticsApplication[];
  totalActiveSeconds: number;
  charted?: number;
}) {
  const most = Math.max(...applications.map(item => item.activeSeconds), 1);
  return (
    <ul className="grid">
      {applications.map((application, index) => {
        const percent = share(application.activeSeconds, totalActiveSeconds);
        const trend = changeAgainst(application.activeSeconds, application.previousActiveSeconds);
        return (
          <li key={application.identityKey} className="border-b last:border-b-0">
            <Link
              href={`/applications/${application.deviceApplicationId}`}
              className="flex items-center gap-3 px-1 py-3 transition-colors hover:bg-muted/50"
            >
              <ApplicationIcon
                applicationId={application.deviceApplicationId}
                displayName={application.displayName}
                hasIcon={application.hasIcon}
              />
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline justify-between gap-3">
                  <p className="min-w-0 truncate text-sm font-medium">
                    {application.displayName}
                    {application.publisher && <span className="font-normal text-muted-foreground"> · {application.publisher}</span>}
                  </p>
                  <p className="shrink-0 text-sm font-medium tabular-nums">{formatDuration(application.activeSeconds)}</p>
                </div>
                <div className="mt-1.5 flex items-center gap-3">
                  <span className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted">
                    <span
                      className="block h-full rounded-full"
                      style={{
                        width: `${Math.max(2, (application.activeSeconds / most) * 100)}%`,
                        background: index < charted ? seriesColor(index) : "var(--chart-other)",
                      }}
                    />
                  </span>
                  <span className="w-10 shrink-0 text-right text-xs text-muted-foreground tabular-nums">{percent}%</span>
                  <span className="w-14 shrink-0 text-right">
                    {trend != null ? <TrendPill change={trend} /> : <span className="text-xs text-muted-foreground">new</span>}
                  </span>
                </div>
              </div>
              <ChevronRight className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            </Link>
          </li>
        );
      })}
    </ul>
  );
}
