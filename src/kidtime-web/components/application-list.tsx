"use client";

import { useSyncExternalStore } from "react";
import Link from "next/link";
import { formatDuration } from "@/lib/format";
import { describeApplicationRules } from "@/lib/schedule";
import type { ApplicationSummary } from "@/lib/types";
import { ApplicationIcon } from "@/components/application-icon";
import { Badge } from "@/components/ui/badge";
import { SwitchOption } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";

const PREFERENCE_KEY = "kidtime.applications.showMicrosoft";

/**
 * The switch is remembered per browser, so the choice survives a reload without needing an
 * account setting. It is read through an external store rather than an effect because the server
 * render cannot see localStorage: the server snapshot is the default, and the real value arrives
 * on hydration without a second render fighting it.
 */
const listeners = new Set<() => void>();

function readPreference() {
  try {
    return window.localStorage.getItem(PREFERENCE_KEY) === "true";
  } catch {
    // A browser that refuses site data simply gets the default.
    return false;
  }
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

function writePreference(value: boolean) {
  try {
    window.localStorage.setItem(PREFERENCE_KEY, String(value));
  } catch {
    // Nothing to do - the choice just does not survive the page.
  }
  for (const listener of listeners) listener();
}

/**
 * The applications table, with Windows' own applications folded away by default. A real
 * controlled PC carries three dozen in-box packages - Clock, Weather, Feedback Hub, Quick Assist -
 * and listing them all first buries Steam, Roblox and Discord under things nobody sets a rule on.
 *
 * An application that already carries a rule is never hidden: losing sight of a limit you set
 * would be a worse failure than a long list. Hiding is presentation only - the server still sends
 * every rule to the PC and still enforces it.
 */
export function ApplicationList({
  applications,
  searching,
}: {
  applications: ApplicationSummary[];
  searching: boolean;
}) {
  const showMicrosoft = useSyncExternalStore(subscribe, readPreference, () => false);

  const hasRule = (app: ApplicationSummary) =>
    app.manuallyBlocked || app.dailyLimitSeconds !== null || app.scheduleConfigured;
  // A parent who typed a name is looking for that thing, so a search shows everything it matched.
  // Answering "nothing found" for an application the switch is hiding would be a dead end.
  const visible = applications.filter(
    app => showMicrosoft || searching || !app.isMicrosoft || hasRule(app),
  );
  const hidden = applications.length - visible.length;

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3 px-4 pb-4 sm:px-6">
        <p className="text-sm text-muted-foreground">
          {visible.length} {visible.length === 1 ? "application" : "applications"}
          {hidden > 0 ? ` · ${hidden} Microsoft ${hidden === 1 ? "app" : "apps"} hidden` : ""}
        </p>
        <SwitchOption
          className="w-auto min-w-56"
          size="sm"
          label="Show Microsoft apps"
          checked={showMicrosoft}
          onCheckedChange={writePreference}
        />
      </div>
      {visible.length ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="pl-4">Application</TableHead>
              <TableHead>Device</TableHead>
              <TableHead>Today</TableHead>
              <TableHead>Rules</TableHead>
              <TableHead className="pr-4">Status</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {visible.map(app => (
              <TableRow key={app.id}>
                <TableCell className="pl-4">
                  <Link
                    href={`/applications/${app.id}`}
                    className="flex min-w-56 items-center gap-3 rounded-md focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none"
                  >
                    <ApplicationIcon applicationId={app.id} displayName={app.displayName} hasIcon={app.hasIcon} />
                    <span className="min-w-0">
                      <span className="block truncate font-medium">{app.displayName}</span>
                      <span className="block max-w-72 truncate text-xs text-muted-foreground">
                        {app.publisher ?? app.executableName}
                      </span>
                    </span>
                  </Link>
                </TableCell>
                <TableCell className="text-muted-foreground">{app.deviceName}</TableCell>
                <TableCell className="font-medium tabular-nums">{formatDuration(app.todayActiveSeconds)}</TableCell>
                {/*
                  A schedule is the rule most of these applications carry, and a column of "No
                  limit" said nothing about it. An application with no rule at all shows a dash
                  rather than a sentence.
                */}
                <TableCell className="text-muted-foreground tabular-nums">{describeApplicationRules(app) ?? "—"}</TableCell>
                <TableCell className="pr-4">
                  {app.manuallyBlocked ? <Badge variant="destructive">Blocked</Badge>
                    : app.scheduleConfigured && !app.withinSchedule ? <Badge variant="outline" className="text-muted-foreground">Off schedule</Badge>
                    : <Badge variant="secondary" className="text-primary">Allowed</Badge>}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <div className="px-6 py-20 text-center">
          <h2 className="font-medium">Nothing to show</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            {hidden > 0
              ? "Every application found here is one Windows ships. Turn on Show Microsoft apps to see them."
              : "Apps appear after discovery or observed foreground use."}
          </p>
        </div>
      )}
    </>
  );
}
