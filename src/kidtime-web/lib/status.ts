import type { ApplicationSummary, DeviceSummary } from "@/lib/types";
import { formatDeviceClock } from "@/lib/schedule";
import type { NowState } from "@/components/status-panel";

/**
 * What the PC is doing right now, in the order the rules decide it: a block from the parent, then
 * a spent daily limit, then the schedule. The same precedence `RuleEvaluator` applies on the PC, so
 * the page never says "Allowed" about a PC the child is locked out of.
 */
export function deviceNowState(device: DeviceSummary): NowState {
  const { schedule } = device;
  if (device.manuallyBlocked) {
    return {
      tone: "bad",
      title: "Blocked by you",
      detail: device.manualBlockUntilUtc ? "For a limited time - unblock it below to end it now." : "Until you unblock it below.",
    };
  }
  if (device.dailyLimitSeconds != null && (device.remainingSeconds ?? 0) <= 0) {
    return { tone: "bad", title: "Daily limit reached", detail: "Available again tomorrow, or now if you give extra time." };
  }
  if (schedule.configured && !schedule.withinWindow) {
    return {
      tone: "warn",
      title: "Outside the schedule",
      detail: schedule.opensAtLocal
        ? `Opens ${formatDeviceClock(schedule.opensAtLocal, schedule.localDate)}.`
        : "No allowed time is coming up in the next week.",
    };
  }
  return {
    tone: "ok",
    title: "Allowed",
    detail: schedule.configured && schedule.closesAtLocal
      ? `Until ${formatDeviceClock(schedule.closesAtLocal, schedule.localDate)}, by the schedule.`
      : "No schedule is limiting it right now.",
  };
}

/** The same question for one application, from the facts the applications list carries. */
export function applicationNowState(
  application: Pick<ApplicationSummary, "manuallyBlocked" | "dailyLimitSeconds" | "todayActiveSeconds" | "scheduleConfigured" | "withinSchedule">,
): NowState {
  if (application.manuallyBlocked) {
    return { tone: "bad", title: "Blocked by you", detail: "It closes whenever it starts. Turn the block off below to allow it." };
  }
  if (application.dailyLimitSeconds != null && application.todayActiveSeconds >= application.dailyLimitSeconds) {
    return { tone: "bad", title: "Daily limit reached", detail: "Available again tomorrow, or now if you give extra time." };
  }
  if (application.scheduleConfigured && !application.withinSchedule) {
    return { tone: "warn", title: "Outside its schedule", detail: "It closes if it is opened before its next allowed range." };
  }
  return {
    tone: "ok",
    title: "Allowed",
    detail: application.scheduleConfigured ? "Inside its schedule right now." : "No schedule is limiting it.",
  };
}
