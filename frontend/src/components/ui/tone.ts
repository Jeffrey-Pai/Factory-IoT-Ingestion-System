import type { Tone } from "../../lib/status";

/** Filled-dot colour per status tone (the accessible carrier — always beside a label). */
export const toneDot: Record<Tone, string> = {
  good: "bg-good",
  warn: "bg-warn",
  serious: "bg-serious",
  critical: "bg-critical",
  neutral: "bg-faint",
};

/** Text colour per tone, for the rare cases the label itself is coloured (dark-safe uses). */
export const toneText: Record<Tone, string> = {
  good: "text-good",
  warn: "text-warn",
  serious: "text-serious",
  critical: "text-critical",
  neutral: "text-muted",
};
