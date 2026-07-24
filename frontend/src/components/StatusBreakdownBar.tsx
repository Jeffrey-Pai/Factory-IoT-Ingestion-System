import type { StatusBreakdown } from "../api/types";
import { statusMeta } from "../lib/status";
import { formatInt } from "../lib/format";
import { cn } from "../lib/cn";
import { toneDot } from "./ui/tone";
import { EmptyState } from "./ui/States";

/**
 * Part-to-whole view of readings by operating status: a single segmented bar
 * (2px surface gaps between segments) plus a legend where each row carries a dot
 * *and* a label, so status is never conveyed by colour alone.
 */
export function StatusBreakdownBar({ breakdown }: { breakdown: StatusBreakdown[] }) {
  const total = breakdown.reduce((sum, b) => sum + b.count, 0);
  const sorted = [...breakdown].sort((a, b) => b.count - a.count);

  if (total === 0) {
    return <EmptyState message="此時間範圍內沒有讀值" className="py-8" />;
  }

  return (
    <div>
      <div className="flex h-3 w-full gap-0.5" role="img" aria-label="讀值狀態分佈">
        {sorted.map((b) => {
          const { tone, label } = statusMeta(b.status);
          const pct = (b.count / total) * 100;
          return (
            <div
              key={b.status}
              className={cn(
                "h-full first:rounded-l-full last:rounded-r-full",
                toneDot[tone]
              )}
              style={{ width: `${pct}%` }}
              title={`${label} · ${pct.toFixed(1)}%`}
            />
          );
        })}
      </div>

      <ul className="mt-4 space-y-2">
        {sorted.map((b) => {
          const { tone, label } = statusMeta(b.status);
          const pct = (b.count / total) * 100;
          return (
            <li key={b.status} className="flex items-center gap-2 text-sm">
              <span className={cn("h-2.5 w-2.5 shrink-0 rounded-full", toneDot[tone])} aria-hidden />
              <span className="text-ink">{label}</span>
              <span className="tabular ml-auto font-medium text-ink">{formatInt(b.count)}</span>
              <span className="tabular w-14 text-right text-xs text-faint">{pct.toFixed(1)}%</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
