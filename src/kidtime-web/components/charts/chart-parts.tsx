"use client";

import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/**
 * The pieces every chart on this panel shares: a recessive grid and axis styling, and one
 * tooltip shape. Keeping them here is what stops two charts on the same page from disagreeing
 * about what a gridline weighs or where a value sits.
 */

export const axisTick = { fontSize: 11, fill: "var(--muted-foreground)" } as const;
export const gridStroke = "var(--chart-grid)";

/** The surface a tooltip is drawn on, shared so a value reads the same wherever it is hovered. */
export function ChartTooltipCard({ title, subtitle, children }: {
  title: string;
  subtitle?: string;
  children?: ReactNode;
}) {
  return (
    <div className="pointer-events-none min-w-40 rounded-lg border bg-popover px-3 py-2 text-xs shadow-md">
      <p className="font-medium text-popover-foreground">{title}</p>
      {subtitle && <p className="mt-0.5 text-muted-foreground">{subtitle}</p>}
      {children && <div className="mt-2 grid gap-1">{children}</div>}
    </div>
  );
}

/** One line of a tooltip: a colour swatch that names the series, its label, and its value. */
export function ChartTooltipRow({ color, label, value, muted }: {
  color?: string;
  label: string;
  value: string;
  muted?: boolean;
}) {
  return (
    <div className="flex items-center gap-2">
      {color && <span className="size-2 shrink-0 rounded-[2px]" style={{ background: color }} aria-hidden="true" />}
      <span className={cn("min-w-0 flex-1 truncate", muted ? "text-muted-foreground" : "text-popover-foreground")}>{label}</span>
      <span className="shrink-0 font-medium tabular-nums text-popover-foreground">{value}</span>
    </div>
  );
}

/**
 * The legend, as ordinary markup rather than a chart primitive, so an application can be named
 * by its own icon next to its colour. Identity is never left to colour alone.
 */
export function ChartLegend({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <ul className={cn("flex flex-wrap items-center gap-x-4 gap-y-2 text-xs", className)}>{children}</ul>
  );
}

export function ChartLegendItem({ color, children }: { color: string; children: ReactNode }) {
  return (
    <li className="flex min-w-0 items-center gap-1.5">
      <span className="size-2.5 shrink-0 rounded-[3px]" style={{ background: color }} aria-hidden="true" />
      <span className="min-w-0 truncate text-muted-foreground">{children}</span>
    </li>
  );
}
