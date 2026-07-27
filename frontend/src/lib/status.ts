/**
 * Operating-status semantics. The simulator currently emits "Running" and
 * "Warning"; the extra cases keep the mapping honest if the backend grows more
 * states. `tone` maps onto the fixed status palette (never a categorical hue).
 */
export type Tone = "good" | "warn" | "serious" | "critical" | "neutral";

export interface StatusMeta {
  tone: Tone;
  /** Human label (zh-Hant). */
  label: string;
}

export function statusMeta(status: string): StatusMeta {
  switch (status.trim().toLowerCase()) {
    case "running":
      return { tone: "good", label: "運轉中" };
    case "warning":
      return { tone: "warn", label: "警告" };
    case "maintenance":
      return { tone: "serious", label: "維護中" };
    case "error":
    case "fault":
    case "critical":
      return { tone: "critical", label: "故障" };
    case "idle":
    case "stopped":
    case "offline":
      return { tone: "neutral", label: "閒置" };
    default:
      return { tone: "neutral", label: status };
  }
}

/** A machine is "live" if its most recent reading is within the threshold. */
export type Freshness = "live" | "stale";

/**
 * Slack for the ingestion path: the worker batches writes on a 2-second timer, and the
 * broker, the batch and the request itself each add a little. A reading is queryable a
 * few seconds after the machine sent it, never instantly.
 */
export const INGESTION_SLACK_MS = 30_000;

/**
 * How old a reading may be before the machine reads as stale, given how often we poll.
 *
 * The poll interval has to be part of this. Data is at its oldest in the instant before
 * the next refresh lands, so a threshold that ignores the cadence makes every machine
 * flicker grey just before each poll at the slower settings — a fleet reporting once a
 * second, drawn as if it had stopped. A paused dashboard adds nothing: it isn't fetching,
 * so it genuinely cannot vouch for anything beyond the ingestion slack.
 */
export function liveThresholdMs(refetchIntervalMs: number | false): number {
  return INGESTION_SLACK_MS + (refetchIntervalMs === false ? 0 : refetchIntervalMs);
}

export function freshness(
  lastSeenIso: string,
  now: number = Date.now(),
  thresholdMs: number = INGESTION_SLACK_MS
): Freshness {
  return now - new Date(lastSeenIso).getTime() <= thresholdMs ? "live" : "stale";
}
