"use client";

import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { formatDuration } from "@/lib/format";
import { durationTicks, formatAxisDuration, formatFullDate, formatWeekday, otherSeriesColor, parseLocalDate, seriesColor } from "@/lib/chart";
import { ChartLegend, ChartLegendItem, ChartTooltipCard, ChartTooltipRow, axisTick, gridStroke } from "@/components/charts/chart-parts";
import { ApplicationIcon } from "@/components/application-icon";
import type { DailyUsage, StatisticsApplication } from "@/lib/types";

const otherKey = "__other";

/**
 * The same days as the daily chart, split by the applications the child actually spent them in.
 *
 * Only the leading few carry a colour of their own; everything past them is one band, because a
 * ninth hue is indistinguishable from one already on screen and a stack of twelve says nothing.
 * The bands sum to the PC's own active time for that day, so this chart and the one above it
 * agree - "other" is the shell, the desktop, and the tail of small applications.
 */
export function ApplicationUsageChart({
  daily,
  applications,
  charted = 6,
  height = 260,
}: {
  daily: DailyUsage[];
  applications: StatisticsApplication[];
  charted?: number;
  height?: number;
}) {
  const series = applications.slice(0, charted).map((application, index) => ({
    key: application.identityKey,
    application,
    color: seriesColor(index),
  }));

  const rows = daily.map(day => {
    const row: Record<string, string | number> = { date: day.date };
    let named = 0;
    for (const item of series) {
      const seconds = item.application.daily.find(entry => entry.date === day.date)?.activeSeconds ?? 0;
      row[item.key] = seconds;
      named += seconds;
    }
    row[otherKey] = Math.max(0, day.activeSeconds - named);
    return row;
  });

  const bands = [...series, { key: otherKey, application: null, color: otherSeriesColor }];
  const scale = durationTicks(Math.max(...daily.map(day => day.activeSeconds), 60));

  return (
    <figure className="m-0">
      <ResponsiveContainer width="100%" height={height}>
        <BarChart data={rows} margin={{ top: 12, right: 8, left: 0, bottom: 0 }} barCategoryGap="24%">
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
              const date = String(props.label ?? "");
              const total = props.payload.reduce((sum, entry) => sum + Number(entry.value ?? 0), 0);
              return (
                <ChartTooltipCard title={formatFullDate(date)} subtitle={formatDuration(total)}>
                  {[...props.payload]
                    .reverse()
                    .filter(entry => Number(entry.value ?? 0) > 0)
                    .map(entry => {
                      const key = String(entry.dataKey);
                      return (
                        <ChartTooltipRow
                          key={key}
                          color={bands.find(band => band.key === key)?.color}
                          label={nameOf(key, series)}
                          value={formatDuration(Number(entry.value ?? 0))}
                        />
                      );
                    })}
                </ChartTooltipCard>
              );
            }}
          />
          {bands.map((band, index) => (
            <Bar
              key={band.key}
              dataKey={band.key}
              stackId="usage"
              fill={band.color}
              // The surface-coloured stroke is the 2px gap between segments, not an outline.
              stroke="var(--card)"
              strokeWidth={1.5}
              isAnimationActive={false}
              maxBarSize={44}
              radius={index === bands.length - 1 ? [4, 4, 0, 0] : 0}
            />
          ))}
        </BarChart>
      </ResponsiveContainer>
      <ChartLegend className="mt-4">
        {series.map(item => (
          <ChartLegendItem key={item.key} color={item.color}>
            <span className="flex items-center gap-1.5">
              <ApplicationIcon
                applicationId={item.application.deviceApplicationId}
                displayName={item.application.displayName}
                hasIcon={item.application.hasIcon}
                size="tiny"
              />
              <span className="truncate">{item.application.displayName}</span>
            </span>
          </ChartLegendItem>
        ))}
        <ChartLegendItem color={otherSeriesColor}>Everything else</ChartLegendItem>
      </ChartLegend>
    </figure>
  );
}

function nameOf(key: string, series: { key: string; application: StatisticsApplication }[]) {
  return series.find(item => item.key === key)?.application.displayName ?? "Everything else";
}
