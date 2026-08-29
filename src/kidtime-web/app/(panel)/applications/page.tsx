import { Search } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import type { ApplicationSummary } from "@/lib/types";
import { ApplicationList } from "@/components/application-list";
import { PageHeader } from "@/components/page-header";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";

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
            <ApplicationList applications={applications} searching={search.trim().length > 0} />
          ) : (
            <div className="px-6 py-20 text-center"><h2 className="font-medium">No applications found</h2><p className="mt-1 text-sm text-muted-foreground">Apps appear after discovery or observed foreground use.</p></div>
          )}
        </CardContent>
      </Card>
    </>
  );
}
