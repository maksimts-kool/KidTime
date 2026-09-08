"use client";

import { Bar, BarChart, CartesianGrid, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { formatDuration } from "@/lib/format";
import { durationTicks, formatAxisDuration, formatFullDate, formatWeekday, parseLocalDate } from "@/lib/chart";
import { ChartTooltipCard, ChartTooltipRow, axisTick, gridStroke } from "@/components/charts/chart-parts";
import type { DailyUsage } from "@/lib/types";

/**
 * Active PC time day by day.
 *
 * One series, so one colour - the brand primary - with two annotations that make a bar mean
 * something on its own: the period's average, and the parent's daily limit where one is set. A
 * bar the child is still adding to is drawn lighter, because "today" is a part-day and reading
 * it as a short day is the mistake this chart most easily invites.
 */
export function DailyUsageChart({
  daily,
  limitSeconds,
  today,
  height = 260,
}: {
  daily: DailyUsage[];
  limitSeconds?: number | null;
  today?: string;
  height?: number;
}) {
  const total = daily.reduce((sum, day) => sum + day.activeSeconds, 0);
  const average = daily.length ? Math.round(total / daily.length) : 0;
  const peak = Math.max(...daily.map(day => day.activeSeconds), limitSeconds ?? 0, 60);
  const scale = durationTicks(peak);

  return (
    <figure className="m-0">
      <ResponsiveContainer width="100%" height={height}>
        <BarChart data={daily} margin={{ top: 12, right: 8, left: 0, bottom: 0 }} barCategoryGap="24%">
          <CartesianGrid vertical={false} stroke={gridStroke} />
          <XAxis
            dataKey="date"
            tickLine={false}
            axisLine={false}
            tickMargin={8}
            interval="preserveStartEnd"
            minTickGap={4}
            tick={axisTick}
            // Past a week the weekdays repeat, so the day of the month is the label that identifies a bar.
            tickFormatter={value => (daily.length > 7 ? parseLocalDate(value).getDate().toString() : formatWeekday(value))}
          />
          <YAxis
            width={44}
            tickLine={false}
            axisLine={false}
            tick={axisTick}
            domain={[0, scale.top]}
            ticks={scale.ticks}
            tickFormatter={formatAxisDuration}
          />
          <Tooltip
            cursor={{ fill: "var(--muted)", fillOpacity: 0.6 }}
            content={props => {
              if (!props.active || !props.payload?.length) return null;
              const seconds = Number(props.payload[0]?.value ?? 0);
              const date = String(props.label ?? "");
              return (
                <ChartTooltipCard title={formatFullDate(date)} subtitle={date === today ? "So far today" : undefined}>
                  <ChartTooltipRow color="var(--primary)" label="Active time" value={formatDuration(seconds)} />
                  {average > 0 && (
                    <ChartTooltipRow
                      label="Against the average"
                      muted
                      value={`${seconds >= average ? "+" : "−"}${formatDuration(Math.abs(seconds - average))}`}
                    />
                  )}
                </ChartTooltipCard>
              );
            }}
          />
          {average > 0 && (
            <ReferenceLine
              y={average}
              stroke="var(--muted-foreground)"
              strokeDasharray="4 4"
              label={{ value: "avg", position: "insideTopLeft", fill: "var(--muted-foreground)", fontSize: 10, dy: -4 }}
            />
          )}
          {limitSeconds != null && limitSeconds <= scale.top && (
            <ReferenceLine
              y={limitSeconds}
              stroke="var(--destructive)"
              strokeDasharray="4 4"
              label={{ value: "limit", position: "insideTopRight", fill: "var(--destructive)", fontSize: 10, dy: -4, dx: -2 }}
            />
          )}
          <Bar dataKey="activeSeconds" radius={[4, 4, 0, 0]} isAnimationActive={false} maxBarSize={44}>
            {daily.map(day => (
              <Cell key={day.date} fill={day.date === today ? "var(--chart-partial)" : "var(--primary)"} />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
      {/*
        Every value in the plot, reachable without a pointer. The axis carries the magnitudes and
        the tooltip carries the detail, but neither is available to a screen reader.
      */}
      <table className="sr-only">
        <caption>Active PC time by day</caption>
        <tbody>
          {daily.map(day => (
            <tr key={day.date}>
              <th scope="row">{formatFullDate(day.date)}</th>
              <td>{formatDuration(day.activeSeconds)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}
