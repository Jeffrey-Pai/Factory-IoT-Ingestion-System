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

export function freshness(
  lastSeenIso: string,
  now: number = Date.now(),
  thresholdMs = 30_000
): Freshness {
  return now - new Date(lastSeenIso).getTime() <= thresholdMs ? "live" : "stale";
}
