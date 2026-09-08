import type { LucideIcon } from "lucide-react";
import { Minus, TrendingDown, TrendingUp } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { cn } from "@/lib/utils";

/**
 * One headline number.
 *
 * A number on its own is rarely a fact a parent can act on - six hours is a lot after a quiet
 * week and ordinary after a loud one - so a card can carry the change against the period before
 * it. The change is deliberately optional and absent rather than zero when there is nothing to
 * compare against: a first week has no trend, and "+100%" would be an invention.
 */
export function StatCard({
  label,
  value,
  detail,
  icon: Icon,
  trend,
  trendLabel,
  /** True when more of this number is the thing a parent is trying to avoid. */
  lessIsBetter = true,
}: {
  label: string;
  value: string;
  detail?: string;
  icon?: LucideIcon;
  trend?: number | null;
  trendLabel?: string;
  lessIsBetter?: boolean;
}) {
  return (
    <Card size="sm">
      <CardHeader className="flex flex-row items-center justify-between gap-2">
        <CardDescription>{label}</CardDescription>
        {Icon && (
          <span className="flex size-7 items-center justify-center rounded-lg bg-primary/10 text-primary">
            <Icon className="size-3.5" aria-hidden="true" />
          </span>
        )}
      </CardHeader>
      <CardContent>
        <CardTitle className="text-xl">{value}</CardTitle>
        <div className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1">
          {trend != null && <TrendPill change={trend} lessIsBetter={lessIsBetter} />}
          {(trendLabel ?? detail) && <p className="text-xs text-muted-foreground">{trendLabel ?? detail}</p>}
        </div>
      </CardContent>
    </Card>
  );
}

export function TrendPill({ change, lessIsBetter = true }: { change: number; lessIsBetter?: boolean }) {
  const flat = change === 0;
  const worse = lessIsBetter ? change > 0 : change < 0;
  const Icon = flat ? Minus : change > 0 ? TrendingUp : TrendingDown;
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-xs font-medium tabular-nums",
        flat ? "bg-muted text-muted-foreground" : worse ? "bg-destructive/10 text-destructive" : "bg-primary/10 text-primary",
      )}
    >
      <Icon className="size-3" aria-hidden="true" />
      {change > 0 ? "+" : ""}{change}%
    </span>
  );
}
