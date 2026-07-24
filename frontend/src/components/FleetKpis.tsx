import { CircleCheck, Cpu, Database, TriangleAlert } from "lucide-react";
import type { FleetStatus } from "../api/types";
import { statusMeta } from "../lib/status";
import { formatInt, formatNumber } from "../lib/format";
import { StatTile } from "./ui/StatTile";

/** Top-line "is the floor OK?" numbers derived from the fleet snapshot. */
export function FleetKpis({ status }: { status: FleetStatus }) {
  const total = status.totalReadings;
  const goodCount = status.breakdown
    .filter((b) => statusMeta(b.status).tone === "good")
    .reduce((sum, b) => sum + b.count, 0);
  const attention = Math.max(total - goodCount, 0);
  const goodPct = total > 0 ? (goodCount / total) * 100 : 0;

  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      <StatTile
        label="回報機台數"
        value={formatInt(status.machineCount)}
        sub="此時間範圍內回報的機台"
        icon={<Cpu className="h-4 w-4" />}
        iconClassName="text-brand"
      />
      <StatTile
        label="總讀值數"
        value={formatInt(total)}
        sub="累計遙測筆數"
        icon={<Database className="h-4 w-4" />}
      />
      <StatTile
        label="運轉正常"
        value={`${formatNumber(goodPct, 1)}%`}
        sub={`${formatInt(goodCount)} 筆讀值`}
        icon={<CircleCheck className="h-4 w-4" />}
        iconClassName="text-good"
      />
      <StatTile
        label="需注意"
        value={formatInt(attention)}
        sub="警告以上的讀值"
        icon={<TriangleAlert className="h-4 w-4" />}
        iconClassName="text-warn"
      />
    </div>
  );
}
