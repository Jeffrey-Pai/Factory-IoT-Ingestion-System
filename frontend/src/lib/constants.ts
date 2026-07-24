/** Rolling-window presets (minutes) shared by the fleet snapshot and machine stats. */
export const WINDOW_OPTIONS = [
  { label: "15 分", value: 15 },
  { label: "1 小時", value: 60 },
  { label: "6 小時", value: 360 },
  { label: "24 小時", value: 1440 },
] as const;

export const DEFAULT_WINDOW_MINUTES = 60;

/** How many raw points to pull for the detail-page time-series charts (API caps at 100). */
export const CHART_POINTS = 100;

/**
 * Fixed value domains for comparable range bars, matching the simulator's output
 * (temperature 20–120 °C, pressure 1–11 bar).
 */
export const TEMP_DOMAIN = { min: 20, max: 120 } as const;
export const PRESSURE_DOMAIN = { min: 1, max: 11 } as const;
