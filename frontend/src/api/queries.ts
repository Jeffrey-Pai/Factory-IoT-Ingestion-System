import { useQuery } from "@tanstack/react-query";
import { api, ApiError } from "./client";
import { useRefresh } from "../live/RefreshProvider";
import type { TelemetryStatistics } from "./types";

/**
 * Query hooks. Each subscribes to the shared refresh cadence so the whole
 * dashboard polls in lockstep (and pauses together). Query keys are structured
 * so React Query dedupes and caches per parameter set.
 */

export function useMachines() {
  const { refetchInterval } = useRefresh();
  return useQuery({
    queryKey: ["machines"],
    queryFn: ({ signal }) => api.machines(signal),
    refetchInterval,
  });
}

export function useFleetStatus(windowMinutes: number) {
  const { refetchInterval } = useRefresh();
  return useQuery({
    queryKey: ["fleet-status", windowMinutes],
    queryFn: ({ signal }) => api.fleetStatus(windowMinutes, signal),
    refetchInterval,
  });
}

export function useMachineStats(machineId: string, windowMinutes: number) {
  const { refetchInterval } = useRefresh();
  return useQuery<TelemetryStatistics | null>({
    queryKey: ["machine-stats", machineId, windowMinutes],
    queryFn: async ({ signal }) => {
      try {
        return await api.stats(machineId, windowMinutes, signal);
      } catch (err) {
        // 404 = the machine reported nothing in the window. That's a valid empty
        // state, not an error — surface it as null so the UI shows "no data".
        if (err instanceof ApiError && err.status === 404) return null;
        throw err;
      }
    },
    refetchInterval,
  });
}

export function useLatestTelemetry(machineId: string, count: number) {
  const { refetchInterval } = useRefresh();
  return useQuery({
    queryKey: ["latest-telemetry", machineId, count],
    queryFn: ({ signal }) => api.latest(machineId, count, signal),
    refetchInterval,
  });
}

export function useWorkerHealth() {
  const { refetchInterval } = useRefresh();
  return useQuery({
    queryKey: ["worker-health"],
    queryFn: ({ signal }) => api.workerHealth(signal),
    // 503 (unhealthy) still carries a JSON body we want to show, but fetch treats
    // it as an error; a short retry smooths transient blips.
    retry: 1,
    refetchInterval,
  });
}
