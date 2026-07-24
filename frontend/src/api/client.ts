import type {
  FleetStatus,
  MachineSummary,
  TelemetryReading,
  TelemetryStatistics,
  WorkerHealth,
} from "./types";

// Empty base = same-origin relative requests, which is what both the Vite dev
// proxy and the nginx production image serve. Override with VITE_API_BASE only
// when the dashboard is hosted on a different origin than the API.
const BASE = (import.meta.env.VITE_API_BASE ?? "").replace(/\/+$/, "");

/** Thrown for any non-2xx response; carries the HTTP status for callers to branch on. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string
  ) {
    super(message);
    this.name = "ApiError";
  }
}

async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, {
      signal,
      headers: { Accept: "application/json" },
    });
  } catch (cause) {
    // Network-level failure (backend down, DNS, CORS, aborted fetch).
    if (cause instanceof DOMException && cause.name === "AbortError") throw cause;
    throw new ApiError(0, "無法連線到後端 API");
  }

  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body && typeof body.error === "string") detail = body.error;
    } catch {
      /* body wasn't JSON — keep the status text */
    }
    throw new ApiError(res.status, detail);
  }

  return (await res.json()) as T;
}

export const api = {
  machines: (signal?: AbortSignal) => apiGet<MachineSummary[]>("/api/v1/machines", signal),

  fleetStatus: (windowMinutes: number, signal?: AbortSignal) =>
    apiGet<FleetStatus>(`/api/v1/fleet/status?windowMinutes=${windowMinutes}`, signal),

  stats: (machineId: string, windowMinutes: number, signal?: AbortSignal) =>
    apiGet<TelemetryStatistics>(
      `/api/v1/telemetry/${encodeURIComponent(machineId)}/stats?windowMinutes=${windowMinutes}`,
      signal
    ),

  latest: (machineId: string, count: number, signal?: AbortSignal) =>
    apiGet<TelemetryReading[]>(
      `/api/v1/telemetry/${encodeURIComponent(machineId)}/latest?count=${count}`,
      signal
    ),

  workerHealth: (signal?: AbortSignal) => apiGet<WorkerHealth>("/health/worker", signal),
};
