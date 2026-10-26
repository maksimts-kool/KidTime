import Link from "next/link";
import type { LucideIcon } from "lucide-react";
import { ArrowRight, Ban, CalendarClock, CircleCheck, Hourglass } from "lucide-react";
import type { ReactNode } from "react";
import { formatDuration } from "@/lib/format";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";

/**
 * How a status reads at a glance. `ok` is nothing to do, `warn` is worth knowing and usually
 * resolves on its own (a PC offline, a sync pending), `bad` needs the parent (no account chosen,
 * faults waiting, the PC shut), and `idle` is a fact with no judgement in it.
 */
export type StatusTone = "ok" | "warn" | "bad" | "idle";

const toneIcon: Record<StatusTone, string> = {
  ok: "bg-primary/10 text-primary",
  warn: "bg-amber-500/15 text-amber-700 dark:text-amber-400",
  bad: "bg-destructive/10 text-destructive",
  idle: "bg-muted text-muted-foreground",
};

/** Only the tones that ask for attention get an outline of their own. */
const toneRing: Record<StatusTone, string> = {
  ok: "",
  warn: "ring-1 ring-amber-500/40",
  bad: "ring-1 ring-destructive/40",
  idle: "",
};

/**
 * A run of cards that belong together - what is true now, then what can be changed. The cards
 * carry their own titles, so the section draws no heading of its own; `label` names it for a
 * screen reader only.
 */
export function PageSection({ id, label, children }: { id?: string; label?: string; children: ReactNode }) {
  return (
    <section id={id} aria-label={label} className="mb-8 grid scroll-mt-20 gap-4">
      {children}
    </section>
  );
}

/**
 * One fact about the PC's health. It says what is true, in a word, and on the line below what it
 * means or what to do - "Offline" alone tells a parent nothing about whether rules still apply.
 */
export function StatusTile({
  label,
  value,
  hint,
  tone,
  icon: Icon,
  href,
}: {
  label: string;
  value: string;
  hint?: ReactNode;
  tone: StatusTone;
  icon: LucideIcon;
  /** Where the fix is, for a tile that asks for one. */
  href?: string;
}) {
  const body = (
    <div className="flex items-start gap-3">
      <span className={cn("flex size-9 shrink-0 items-center justify-center rounded-lg", toneIcon[tone])}><Icon className="size-4" /></span>
      <div className="min-w-0">
        <p className="text-xs text-muted-foreground">{label}</p>
        <p className="truncate text-sm font-medium">{value}</p>
        {hint && <p className="mt-0.5 text-xs text-muted-foreground">{hint}</p>}
      </div>
      {href && <ArrowRight className="ml-auto size-4 shrink-0 self-center text-muted-foreground" aria-hidden />}
    </div>
  );
  const className = cn("rounded-lg bg-muted/50 p-3", toneRing[tone], href && "transition-colors hover:bg-muted");
  return href
    ? <Link href={href} className={className}>{body}</Link>
    : <div className={className}>{body}</div>;
}

export type NowState = {
  tone: StatusTone;
  title: string;
  detail: string;
};

const stateIcon: Record<StatusTone, LucideIcon> = {
  ok: CircleCheck,
  warn: CalendarClock,
  bad: Ban,
  idle: Hourglass,
};

/**
 * What a PC or an application is doing right now - the one question a parent opens the page to
 * answer - with today's time against its limit beneath it.
 */
export function NowCard({
  state,
  usedSeconds,
  limitSeconds,
  badges,
  children,
}: {
  state: NowState;
  usedSeconds: number;
  /** The limit in force today, special periods included; null for none. */
  limitSeconds: number | null;
  badges?: ReactNode;
  /** Anything else about today, such as the schedule drawn as a bar. */
  children?: ReactNode;
}) {
  const Icon = stateIcon[state.tone];
  const percent = limitSeconds ? Math.min(100, usedSeconds / limitSeconds * 100) : 0;
  const left = limitSeconds != null ? Math.max(0, limitSeconds - usedSeconds) : null;
  return (
    <Card className={toneRing[state.tone]}>
      <CardContent className="grid gap-5 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] md:items-start">
        <div className="flex items-start gap-3">
          <span className={cn("flex size-11 shrink-0 items-center justify-center rounded-xl", toneIcon[state.tone])}><Icon className="size-5" /></span>
          <div className="min-w-0">
            <p className="text-xs text-muted-foreground">Right now</p>
            <p className="text-xl font-semibold tracking-tight">{state.title}</p>
            <p className="text-sm text-muted-foreground">{state.detail}</p>
            {badges && <div className="mt-2 flex flex-wrap gap-1.5">{badges}</div>}
          </div>
        </div>
        <div>
          <div className="flex items-baseline justify-between gap-2">
            <p className="text-xs text-muted-foreground">Used today</p>
            <p className="text-xs text-muted-foreground tabular-nums">
              {limitSeconds != null ? `${formatDuration(left)} left of ${formatDuration(limitSeconds)}` : "No daily limit"}
            </p>
          </div>
          <p className="mt-1 text-2xl font-semibold tabular-nums">{formatDuration(usedSeconds)}</p>
          {limitSeconds != null && (
            <Progress className="mt-2" value={percent} aria-label={`${Math.round(percent)} percent of today's limit used`} />
          )}
        </div>
        {children && <div className="md:col-span-2">{children}</div>}
      </CardContent>
    </Card>
  );
}

/** A small pill for the facts beside the state: a holiday in force, a schedule open or shut. */
export function StateBadge({ children, tone = "idle" }: { children: ReactNode; tone?: StatusTone }) {
  return (
    <Badge variant="outline" className={cn("font-normal", tone === "ok" && "text-primary", tone === "warn" && "text-amber-700 dark:text-amber-400", tone === "bad" && "text-destructive")}>
      {children}
    </Badge>
  );
}

/** What a PC or an application is doing right now, as one badge for a list or a card header. */
export function NowBadge({ state }: { state: NowState }) {
  return <StateBadge tone={state.tone}>{state.title}</StateBadge>;
}

/** One setting that is a button rather than a field: what it does on the left, the button on the right. */
export function ActionRow({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="min-w-0">
        <p className="text-sm font-medium">{title}</p>
        <p className="text-sm text-muted-foreground">{description}</p>
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  );
}
