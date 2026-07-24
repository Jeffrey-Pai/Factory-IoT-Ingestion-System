import { cn } from "../../lib/cn";

interface RangeBarProps {
  min: number;
  avg: number;
  max: number;
  /** Fixed value domain so bars are comparable across rows. */
  domainMin: number;
  domainMax: number;
  className?: string;
}

/**
 * A slim min–max band with an average marker, on a fixed domain so every row's
 * bar is directly comparable. One hue (sequential), no colour identity needed.
 */
export function RangeBar({ min, avg, max, domainMin, domainMax, className }: RangeBarProps) {
  const span = Math.max(domainMax - domainMin, 1e-6);
  const pct = (v: number) => Math.min(100, Math.max(0, ((v - domainMin) / span) * 100));
  const left = pct(min);
  const width = Math.max(pct(max) - left, 1.5);
  const mid = pct(avg);

  return (
    <div
      className={cn("relative h-1.5 w-full rounded-full bg-hairline/70", className)}
      role="img"
      aria-label={`最小 ${min}、平均 ${avg}、最大 ${max}`}
    >
      <div
        className="absolute inset-y-0 rounded-full bg-brand/25"
        style={{ left: `${left}%`, width: `${width}%` }}
      />
      <div
        className="absolute top-1/2 h-2.5 w-2.5 -translate-x-1/2 -translate-y-1/2 rounded-full bg-brand ring-2 ring-surface"
        style={{ left: `${mid}%` }}
      />
    </div>
  );
}
