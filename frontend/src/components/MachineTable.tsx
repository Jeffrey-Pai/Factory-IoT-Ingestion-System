import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { ArrowDown, ArrowUp, ChevronsUpDown, ChevronRight, Search } from "lucide-react";
import type { MachineSummary } from "../api/types";
import { formatInt, formatPressure, formatRelative, formatTemp } from "../lib/format";
import { freshness } from "../lib/status";
import { PRESSURE_DOMAIN, TEMP_DOMAIN } from "../lib/constants";
import { cn } from "../lib/cn";
import { RangeBar } from "./ui/RangeBar";
import { LiveDot } from "./ui/LiveDot";
import { EmptyState } from "./ui/States";

type SortKey = "machineId" | "sampleCount" | "avgTemperature" | "avgPressure" | "lastSeen";
type SortDir = "asc" | "desc";

function sortValue(m: MachineSummary, key: SortKey): string | number {
  if (key === "machineId") return m.machineId;
  if (key === "lastSeen") return new Date(m.lastSeen).getTime();
  return m[key];
}

export function MachineTable({ machines }: { machines: MachineSummary[] }) {
  const navigate = useNavigate();
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<{ key: SortKey; dir: SortDir }>({
    key: "machineId",
    dir: "asc",
  });
  const now = Date.now();

  const rows = useMemo(() => {
    const q = query.trim().toLowerCase();
    const filtered = q
      ? machines.filter((m) => m.machineId.toLowerCase().includes(q))
      : machines;
    const dir = sort.dir === "asc" ? 1 : -1;
    return [...filtered].sort((a, b) => {
      const av = sortValue(a, sort.key);
      const bv = sortValue(b, sort.key);
      if (av < bv) return -1 * dir;
      if (av > bv) return 1 * dir;
      return 0;
    });
  }, [machines, query, sort]);

  const toggleSort = (key: SortKey) =>
    setSort((prev) =>
      prev.key === key
        ? { key, dir: prev.dir === "asc" ? "desc" : "asc" }
        : { key, dir: key === "machineId" ? "asc" : "desc" }
    );

  return (
    <div>
      <div className="flex items-center justify-between gap-3 px-4 pb-3 sm:px-5">
        <label className="relative flex-1 sm:max-w-xs">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-faint" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="搜尋機台編號…"
            className="w-full rounded-lg bg-surface-2 py-1.5 pl-8 pr-3 text-sm text-ink ring-1 ring-hairline placeholder:text-faint focus:outline-none"
          />
        </label>
        <span className="tabular shrink-0 text-xs text-muted">{rows.length} 台</span>
      </div>

      <div className="overflow-x-auto">
        <table className="w-full min-w-[640px] border-collapse text-sm">
          <thead>
            <tr className="border-y border-hairline text-left text-xs text-muted">
              <SortableTh
                label="機台"
                active={sort.key === "machineId"}
                dir={sort.dir}
                onClick={() => toggleSort("machineId")}
                className="pl-4 sm:pl-5"
              />
              <SortableTh
                label="樣本數"
                active={sort.key === "sampleCount"}
                dir={sort.dir}
                onClick={() => toggleSort("sampleCount")}
                align="right"
              />
              <SortableTh
                label="溫度 (°C)"
                active={sort.key === "avgTemperature"}
                dir={sort.dir}
                onClick={() => toggleSort("avgTemperature")}
              />
              <SortableTh
                label="壓力 (bar)"
                active={sort.key === "avgPressure"}
                dir={sort.dir}
                onClick={() => toggleSort("avgPressure")}
              />
              <SortableTh
                label="最後回報"
                active={sort.key === "lastSeen"}
                dir={sort.dir}
                onClick={() => toggleSort("lastSeen")}
                align="right"
              />
              <th className="w-8" />
            </tr>
          </thead>
          <tbody>
            {rows.map((m) => {
              const live = freshness(m.lastSeen, now) === "live";
              return (
                <tr
                  key={m.machineId}
                  onClick={() => navigate(`/machines/${encodeURIComponent(m.machineId)}`)}
                  className="group cursor-pointer border-b border-hairline/60 transition-colors last:border-0 hover:bg-surface-2/70"
                >
                  <td className="py-2.5 pl-4 pr-3 sm:pl-5">
                    <span className="flex items-center gap-2">
                      <LiveDot live={live} />
                      <Link
                        to={`/machines/${encodeURIComponent(m.machineId)}`}
                        onClick={(e) => e.stopPropagation()}
                        className="font-medium text-ink hover:text-brand"
                      >
                        {m.machineId}
                      </Link>
                    </span>
                  </td>
                  <td className="tabular px-3 py-2.5 text-right text-muted">
                    {formatInt(m.sampleCount)}
                  </td>
                  <td className="px-3 py-2.5">
                    <MetricCell
                      avg={formatTemp(m.avgTemperature)}
                      min={m.minTemperature}
                      max={m.maxTemperature}
                      avgValue={m.avgTemperature}
                      domain={TEMP_DOMAIN}
                    />
                  </td>
                  <td className="px-3 py-2.5">
                    <MetricCell
                      avg={formatPressure(m.avgPressure)}
                      min={m.minPressure}
                      max={m.maxPressure}
                      avgValue={m.avgPressure}
                      domain={PRESSURE_DOMAIN}
                    />
                  </td>
                  <td className="tabular px-3 py-2.5 text-right text-muted">
                    {formatRelative(m.lastSeen, now)}
                  </td>
                  <td className="pr-3 text-faint">
                    <ChevronRight className="h-4 w-4 transition-transform group-hover:translate-x-0.5 group-hover:text-muted" />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {rows.length === 0 && <EmptyState message="找不到符合的機台" className="py-10" />}
    </div>
  );
}

function MetricCell({
  avg,
  min,
  max,
  avgValue,
  domain,
}: {
  avg: string;
  min: number;
  max: number;
  avgValue: number;
  domain: { min: number; max: number };
}) {
  return (
    <div className="flex items-center gap-3">
      <span className="tabular w-16 shrink-0 font-medium text-ink">{avg}</span>
      <RangeBar
        min={min}
        avg={avgValue}
        max={max}
        domainMin={domain.min}
        domainMax={domain.max}
        className="hidden max-w-[140px] flex-1 md:block"
      />
    </div>
  );
}

function SortableTh({
  label,
  active,
  dir,
  onClick,
  align = "left",
  className,
}: {
  label: string;
  active: boolean;
  dir: SortDir;
  onClick: () => void;
  align?: "left" | "right";
  className?: string;
}) {
  return (
    <th className={cn("py-2 font-medium", align === "right" ? "pr-3 text-right" : "px-3", className)}>
      <button
        type="button"
        onClick={onClick}
        className={cn(
          "inline-flex items-center gap-1 transition-colors hover:text-ink",
          align === "right" && "flex-row-reverse",
          active && "text-ink"
        )}
      >
        {label}
        {active ? (
          dir === "asc" ? (
            <ArrowUp className="h-3.5 w-3.5" />
          ) : (
            <ArrowDown className="h-3.5 w-3.5" />
          )
        ) : (
          <ChevronsUpDown className="h-3.5 w-3.5 opacity-40" />
        )}
      </button>
    </th>
  );
}
