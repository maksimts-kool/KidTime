import type { ApplicationSummary, DeviceSummary } from "@/lib/types";
import { formatDeviceClock } from "@/lib/schedule";
import type { NowState } from "@/components/status-panel";

const limitReached: NowState = {
  tone: "bad",
  title: "Daily limit reached",
  detail: "Available again tomorrow, or now if you give extra time.",
};

/**
 * Extra time a parent gave while the scope was blocked or outside its schedule opens it until the
 * time it names, counted from the decision. It never hands over a spent daily limit - the same
 * grant already raised that - so this is only ever reached once the limit has been checked.
 */
function extraTimeState(until: string, localDate: string, blocked: boolean): NowState {
  return {
    tone: "ok",
    title: "Open on extra time",
    detail: `Until ${formatDeviceClock(until, localDate)}, then ${blocked ? "your block" : "the schedule"} applies again.`,
  };
}

/**
 * What the PC is doing right now, in the order the rules decide it: a block from the parent, then
 * a spent daily limit, then the schedule - with extra time suspending the first and the last. The
 * same precedence `RuleEvaluator` applies on the PC, so the page never says "Allowed" about a PC
 * the child is locked out of, nor "Blocked" about one the child has just been let back into.
 */
export function deviceNowState(device: DeviceSummary): NowState {
  const { schedule, extraTimeUntilLocal: extraTime } = device;
  const outsideSchedule = schedule.configured && !schedule.withinWindow;
  if (device.manuallyBlocked && !extraTime) {
    return {
      tone: "bad",
      title: "Blocked by you",
      detail: device.manualBlockUntilUtc ? "For a limited time - unblock it below to end it now." : "Until you unblock it below.",
    };
  }
  if (device.dailyLimitSeconds != null && (device.remainingSeconds ?? 0) <= 0) return limitReached;
  if (extraTime && (device.manuallyBlocked || outsideSchedule)) {
    return extraTimeState(extraTime, schedule.localDate, device.manuallyBlocked);
  }
  if (outsideSchedule) {
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
  application: Pick<
    ApplicationSummary,
    "manuallyBlocked" | "dailyLimitSeconds" | "todayActiveSeconds" | "scheduleConfigured" | "withinSchedule" | "localDate" | "extraTimeUntilLocal"
  >,
): NowState {
  const extraTime = application.extraTimeUntilLocal;
  const outsideSchedule = application.scheduleConfigured && !application.withinSchedule;
  if (application.manuallyBlocked && !extraTime) {
    return { tone: "bad", title: "Blocked by you", detail: "It closes whenever it starts. Turn the block off below to allow it." };
  }
  if (application.dailyLimitSeconds != null && application.todayActiveSeconds >= application.dailyLimitSeconds) return limitReached;
  if (extraTime && (application.manuallyBlocked || outsideSchedule)) {
    return extraTimeState(extraTime, application.localDate, application.manuallyBlocked);
  }
  if (outsideSchedule) {
    return { tone: "warn", title: "Outside its schedule", detail: "It closes if it is opened before its next allowed range." };
  }
  return {
    tone: "ok",
    title: "Allowed",
    detail: application.scheduleConfigured ? "Inside its schedule right now." : "No schedule is limiting it.",
  };
}
