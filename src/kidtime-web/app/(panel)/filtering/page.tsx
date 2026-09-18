import { ExternalLink, Globe, ShieldCheck, ShieldOff, TriangleAlert } from "lucide-react";
import { backendFetch } from "@/lib/backend";
import type { DnsFilterCategoryKind, DnsFiltering } from "@/lib/types";
import { PageHeader } from "@/components/page-header";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";

/**
 * The same categories the child's Internet tab names, in the parent's language. They are read out
 * of the block lists' addresses on the server; a list whose address says nothing lands in "Other"
 * rather than being guessed at.
 */
const categoryLabels: Record<DnsFilterCategoryKind, string> = {
  Malware: "Dangerous sites",
  Adult: "Adult sites",
  Gambling: "Gambling",
  Social: "Social networks",
  Ads: "Ads",
  Trackers: "Trackers",
  Other: "Other lists",
};

/**
 * The parent's door to the DNS server, and nothing more. Web filtering is configured in
 * Technitium's own console - lists, groups and timetables all live there - and a second editor in
 * this panel would be a second place the same setting lives and a second way for it to disagree
 * with itself. So this page says whether filtering is on and opens the console.
 *
 * The child's own PC is where the filtering is actually described, on the Internet tab of the
 * KidTime window: that is the person who needs to know what is blocked and until when.
 */
export default async function FilteringPage() {
  const filtering = await backendFetch<DnsFiltering>("/api/dns");
  const tone = describe(filtering);

  return (
    <>
      <PageHeader
        eyebrow="Home network"
        title="Web filtering"
        description="Website filtering runs on your Technitium DNS server and covers every device in the house, not only the enrolled PCs. Set it up there; this page only tells you where it stands."
      />

      <Card>
        <CardContent className="flex flex-col gap-6 px-6 py-8 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex min-w-0 items-start gap-4">
            <span className={`flex size-12 shrink-0 items-center justify-center rounded-xl ${tone.chip}`}>
              <tone.icon className="size-6" aria-hidden="true" />
            </span>
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <h2 className="font-medium">{tone.title}</h2>
                <Badge variant={tone.badge}>{tone.badgeLabel}</Badge>
              </div>
              <p className="mt-1.5 max-w-xl text-sm leading-relaxed text-muted-foreground">{tone.detail}</p>
              {filtering.categories.length > 0 && (
                <>
                  <p className="mt-3 text-xs font-medium tracking-wide text-muted-foreground uppercase">Always blocked</p>
                  <ul className="mt-1.5 flex flex-wrap gap-1.5">
                    {filtering.categories.map(category => (
                      <li key={category.kind}>
                        <Badge variant="outline" className="font-normal">
                          {categoryLabels[category.kind]}
                          {category.listCount > 1 && <span className="text-muted-foreground">×{category.listCount}</span>}
                        </Badge>
                      </li>
                    ))}
                  </ul>
                </>
              )}
              {tone.footnote && <p className="mt-3 text-xs text-muted-foreground">{tone.footnote}</p>}
            </div>
          </div>
          {filtering.consoleUrl ? (
            <Button
              size="lg"
              className="shrink-0"
              render={
                <a href={filtering.consoleUrl} target="_blank" rel="noreferrer noopener">
                  Open DNS console <ExternalLink data-icon="inline-end" />
                </a>
              }
            />
          ) : null}
        </CardContent>
      </Card>

      <Card className="mt-4">
        <CardContent className="grid gap-4 px-6 py-6 md:grid-cols-2">
          <div className="rounded-lg border bg-muted/40 p-4">
            <ShieldCheck className="mb-3 size-5 text-primary" aria-hidden="true" />
            <h3 className="text-sm font-medium">What the child sees</h3>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              The KidTime window on the controlled PC gets an Internet tab listing what is blocked all the time and
              which sites have hours of their own, with the time they come back. It is read-only there too.
            </p>
          </div>
          <div className="rounded-lg border bg-muted/40 p-4">
            <Globe className="mb-3 size-5 text-muted-foreground" aria-hidden="true" />
            <h3 className="text-sm font-medium">What KidTime never reads</h3>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              Query logs. KidTime reads the filter&apos;s configuration and not one line of what was looked up, so no
              browsing history reaches this panel, the database, or the child&apos;s PC.
            </p>
          </div>
        </CardContent>
      </Card>
    </>
  );
}

function describe(filtering: DnsFiltering) {
  const chip = filtering.isStale ? "bg-amber-500/10 text-amber-600" : null;
  const group = filtering.groupName ? ` “${filtering.groupName}”` : "";
  const sites = filtering.siteGroups.length
    ? `${filtering.siteGroups.length} ${filtering.siteGroups.length === 1 ? "group of sites is" : "groups of sites are"} on a timetable of their own.`
    : "No group of sites is on a timetable of its own.";

  switch (filtering.state) {
    case "Active":
      return {
        icon: filtering.isStale ? TriangleAlert : ShieldCheck,
        chip: chip ?? "bg-primary/10 text-primary",
        badge: "default" as const,
        badgeLabel: "On",
        title: "Web filtering is on",
        detail: `Filtering group${group}. ${sites}`,
        footnote: lastRead(filtering.retrievedAtUtc, filtering.isStale),
      };
    case "Inactive":
      return {
        icon: filtering.isStale ? TriangleAlert : ShieldOff,
        chip: chip ?? "bg-muted text-muted-foreground",
        badge: "secondary" as const,
        badgeLabel: "Off",
        title: "Web filtering is off",
        detail:
          "The DNS server answers but nothing is being filtered for this household — blocking is switched off, or no filtering group covers it.",
        footnote: lastRead(filtering.retrievedAtUtc, filtering.isStale),
      };
    case "Unreachable":
      return {
        icon: TriangleAlert,
        chip: "bg-amber-500/10 text-amber-600",
        badge: "secondary" as const,
        badgeLabel: "No answer",
        title: "The DNS server did not answer",
        detail:
          "Filtering itself is unaffected — it runs on the DNS server, not here. KidTime has not managed to read it once, so there is nothing to describe yet; the usual cause is the certificate pin or the credentials.",
        footnote: null,
      };
    default:
      return {
        icon: Globe,
        chip: "bg-muted text-muted-foreground",
        badge: "outline" as const,
        badgeLabel: "Not set up",
        title: "No DNS server is connected",
        detail:
          "Fill in KIDTIME_DNS_API_URL, KIDTIME_DNS_USERNAME and KIDTIME_DNS_PASSWORD in the server's .env to point KidTime at your Technitium DNS Companion. Until then the child's PC shows no Internet tab.",
        footnote: null,
      };
  }
}

/** A chart's dates are formatted in a named locale, and so is this - the panel is English. */
function lastRead(retrievedAtUtc: string | null, isStale = false) {
  if (!retrievedAtUtc) return null;
  const read = new Date(retrievedAtUtc).toLocaleString("en-GB", {
    dateStyle: "medium",
    timeStyle: "short",
    timeZone: "UTC",
  });
  return isStale
    ? `The DNS server is not answering — last read ${read} UTC`
    : `Last read ${read} UTC`;
}
