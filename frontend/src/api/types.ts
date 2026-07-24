/**
 * TypeScript mirrors of the backend contracts. The ASP.NET API serialises records
 * and entities with System.Text.Json's web defaults (camelCase), so these property
 * names match the JSON on the wire. Timestamps are ISO-8601 strings.
 */

/** GET /api/v1/machines — one rolled-up row per machine. */
export interface MachineSummary {
  machineId: string;
  sampleCount: number;
  firstSeen: string;
  lastSeen: string;
  minTemperature: number;
  maxTemperature: number;
  avgTemperature: number;
  minPressure: number;
  maxPressure: number;
  avgPressure: number;
}

/** GET /api/v1/telemetry/{id}/stats — aggregates over a rolling window. */
export interface TelemetryStatistics {
  machineId: string;
  sampleCount: number;
  firstReading: string;
  lastReading: string;
  minTemperature: number;
  maxTemperature: number;
  avgTemperature: number;
  minPressure: number;
  maxPressure: number;
  avgPressure: number;
}

/** One status bucket inside a fleet snapshot. */
export interface StatusBreakdown {
  status: string;
  count: number;
}

/** GET /api/v1/fleet/status — fleet-wide health snapshot over a window. */
export interface FleetStatus {
  machineCount: number;
  totalReadings: number;
  breakdown: StatusBreakdown[];
}

/** GET /api/v1/telemetry/{id}/latest — raw wide-table snapshots, newest first. */
export interface TelemetryReading {
  id: string;
  machineId: string;
  temperature: number;
  pressure: number;
  status: string;
  timestamp: string;
}

/** GET /api/v1/sensors/{id}/readings — normalised per-sensor readings. */
export interface SensorReading {
  machineId: string;
  sensorType: string;
  value: number;
  unit: string;
  timestamp: string;
}

/** GET /health/worker — ingestion worker liveness detail (503 when unhealthy). */
export interface WorkerHealth {
  isHealthy: boolean;
  lastMessageReceived: string | null;
  lastBatchFlushed: string | null;
  /** .NET TimeSpan serialised as a string, e.g. "00:00:03.1234567". */
  timeSinceLastMessage: string | null;
  timeSinceLastFlush: string | null;
}
