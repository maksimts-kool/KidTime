import Link from "next/link";
import { Search } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import { formatDuration } from "@/lib/format";
import type { ApplicationSummary } from "@/lib/types";
import { ApplicationIcon } from "@/components/application-icon";
import { PageHeader } from "@/components/page-header";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";

export default async function ApplicationsPage({ searchParams }: { searchParams: Promise<{ search?: string }> }) {
  const search = (await searchParams).search ?? "";
  const applications = await backendFetch<ApplicationSummary[]>(`/api/applications${search ? `?search=${encodeURIComponent(search)}` : ""}`);
  const searchControl = (
    <form className="relative w-full sm:w-72">
      <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
      <Input className="pl-9" name="search" defaultValue={search} placeholder="Search applications" aria-label="Search applications" />
    </form>
  );
  return (
    <>
      <PageHeader eyebrow="Discovery & control" title="Applications" description="Installed and observed user applications on enrolled PCs." actions={searchControl} />
      <Card>
        <CardContent className="px-0">
          {applications.length ? (
            <Table>
              <TableHeader><TableRow><TableHead className="pl-4">Application</TableHead><TableHead>Device</TableHead><TableHead>Today</TableHead><TableHead>Limit</TableHead><TableHead className="pr-4">Status</TableHead></TableRow></TableHeader>
              <TableBody>
                {applications.map(app => (
                  <TableRow key={app.id}>
                    <TableCell className="pl-4">
                      <Link href={`/applications/${app.id}`} className="flex min-w-56 items-center gap-3 rounded-md focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none">
                        <ApplicationIcon applicationId={app.id} displayName={app.displayName} hasIcon={app.hasIcon} />
                        <span className="min-w-0"><span className="block truncate font-medium">{app.displayName}</span><span className="block max-w-72 truncate text-xs text-muted-foreground">{app.publisher ?? app.executableName}</span></span>
                      </Link>
                    </TableCell>
                    <TableCell className="text-muted-foreground">{app.deviceName}</TableCell>
                    <TableCell className="font-medium tabular-nums">{formatDuration(app.todayActiveSeconds)}</TableCell>
                    <TableCell className="text-muted-foreground tabular-nums">{formatDuration(app.dailyLimitSeconds)}</TableCell>
                    <TableCell className="pr-4"><Badge variant={app.manuallyBlocked ? "destructive" : "secondary"}>{app.manuallyBlocked ? "Blocked" : "Allowed"}</Badge></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <div className="px-6 py-20 text-center"><h2 className="font-medium">No applications found</h2><p className="mt-1 text-sm text-muted-foreground">Apps appear after discovery or observed foreground use.</p></div>
          )}
        </CardContent>
      </Card>
    </>
  );
}
