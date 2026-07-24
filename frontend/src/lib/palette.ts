import type { Theme } from "../theme/ThemeProvider";

/**
 * Literal colour values for charts. Recharts takes colours as string props, so it
 * can't read the CSS variables in index.css — these mirror the same validated
 * data-viz palette per theme. Series hues are stepped for each surface; the four
 * status hues are fixed (identical in both modes).
 */
export interface ChartPalette {
  /** Categorical / single-series hues. */
  temperature: string; // orange — warm, reads as "heat"
  pressure: string; // blue — the palette's slot-1 accent
  /** Chart chrome. */
  grid: string;
  axis: string;
  surface: string;
  tooltipBorder: string;
  /** Fixed status palette. */
  good: string;
  warn: string;
  serious: string;
  critical: string;
}

const LIGHT: ChartPalette = {
  temperature: "#eb6834",
  pressure: "#2a78d6",
  grid: "#e1e0d9",
  axis: "#898781",
  surface: "#fcfcfb",
  tooltipBorder: "rgba(11,11,11,0.10)",
  good: "#0ca30c",
  warn: "#fab219",
  serious: "#ec835a",
  critical: "#d03b3b",
};

const DARK: ChartPalette = {
  temperature: "#d95926",
  pressure: "#3987e5",
  grid: "#2c2c2a",
  axis: "#898781",
  surface: "#1a1a19",
  tooltipBorder: "rgba(255,255,255,0.10)",
  good: "#0ca30c",
  warn: "#fab219",
  serious: "#ec835a",
  critical: "#d03b3b",
};

export function paletteFor(theme: Theme): ChartPalette {
  return theme === "dark" ? DARK : LIGHT;
}
