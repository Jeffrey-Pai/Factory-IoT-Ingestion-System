import { useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ArrowLeft, Gauge, Hash, Thermometer, Clock } from "lucide-react";
import { useLatestTelemetry, useMachineStats } from "../api/queries";
import { CHART_POINTS, DEFAULT_WINDOW_MINUTES } from "../lib/constants";
import {
  formatDateTime,
  formatInt,
  formatPressure,
  formatRelative,
  formatTemp,
} from "../lib/format";
import { freshness, liveThresholdMs } from "../lib/status";
import { useNow } from "../live/useNow";
import { useRefresh } from "../live/RefreshProvider";
import { Card, CardHeader } from "../components/ui/Card";
import { StatTile } from "../components/ui/StatTile";
import { StatusBadge } from "../components/ui/StatusBadge";
import { LiveDot } from "../components/ui/LiveDot";
import { EmptyState, ErrorState, Loading } from "../components/ui/States";
import { WindowSelector } from "../components/WindowSelector";
import { TelemetryChart } from "../components/TelemetryChart";

export function MachineDetail() {
  const { machineId = "" } = useParams();
  const [windowMinutes, setWindowMinutes] = useState<number>(DEFAULT_WINDOW_MINUTES);
  const now = useNow();
  const { refetchInterval } = useRefresh();

  const statsQ = useMachineStats(machineId, windowMinutes);
  const latestQ = useLatestTelemetry(machineId, CHART_POINTS);

  const readings = useMemo(() => latestQ.data ?? [], [latestQ.data]);
  const sorted = useMemo(
    () =>
      [...readings].sort(
        (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
      ),
    [readings]
  );
  const tempPoints = useMemo(
    () => sorted.map((r) => ({ t: new Date(r.timestamp).getTime(), value: r.temperature })),
    [sorted]
  );
  const pressurePoints = useMemo(
    () => sorted.map((r) => ({ t: new Date(r.timestamp).getTime(), value: r.pressure })),
    [sorted]
  );

  const stats = statsQ.data ?? null;
  const latest = readings[0];
  const lastSeenIso = latest?.timestamp ?? stats?.lastReading;
  const live = lastSeenIso
    ? freshness(lastSeenIso, now, liveThresholdMs(refetchInterval)) === "live"
    : false;

  const initialLoading = latestQ.isLoading && statsQ.isLoading;
  const hardError = latestQ.isError && statsQ.isError;
  const noData = !initialLoading && !hardError && readings.length === 0 && stats === null;

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <Link
            to="/"
            className="mb-1 inline-flex items-center gap-1 text-xs text-muted hover:text-ink"
          >
            <ArrowLeft className="h-3.5 w-3.5" /> 返回總覽
          </Link>
          <div className="flex flex-wrap items-center gap-2.5">
            <h1 className="text-lg font-semibold text-ink">{machineId}</h1>
            {lastSeenIso && (
              <span className="inline-flex items-center gap-1.5 text-xs text-muted">
                <LiveDot live={live} />
                {live ? "即時回報中" : `最後回報 ${formatRelative(lastSeenIso, now)}`}
              </span>
            )}
            {latest && <StatusBadge status={latest.status} />}
          </div>
        </div>
        <WindowSelector value={windowMinutes} onChange={setWindowMinutes} />
      </div>

      {initialLoading ? (
        <Card className="p-2">
          <Loading label="載入機台資料…" />
        </Card>
      ) : hardError ? (
        <Card className="p-2">
          <ErrorState
            message={(latestQ.error as Error)?.message ?? (statsQ.error as Error)?.message}
            onRetry={() => {
              latestQ.refetch();
              statsQ.refetch();
            }}
          />
        </Card>
      ) : noData ? (
        <Card className="p-2">
          <EmptyState message={`「${machineId}」在所選時間範圍內沒有資料`} />
        </Card>
      ) : (
        <>
          {/* KPI tiles */}
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <StatTile
              label={`樣本數（近 ${windowMinutes} 分）`}
              value={stats ? formatInt(stats.sampleCount) : "—"}
              sub={stats ? "筆遙測讀值" : "此範圍內無資料"}
              icon={<Hash className="h-4 w-4" />}
              iconClassName="text-brand"
            />
            <StatTile
              label="平均溫度"
              value={stats ? formatTemp(stats.avgTemperature) : "—"}
              sub={
                stats
                  ? `${formatTemp(stats.minTemperature)} ~ ${formatTemp(stats.maxTemperature)}`
                  : undefined
              }
              icon={<Thermometer className="h-4 w-4" />}
              iconClassName="text-serious"
            />
            <StatTile
              label="平均壓力"
              value={stats ? formatPressure(stats.avgPressure) : "—"}
              sub={
                stats
                  ? `${formatPressure(stats.minPressure)} ~ ${formatPressure(stats.maxPressure)}`
                  : undefined
              }
              icon={<Gauge className="h-4 w-4" />}
              iconClassName="text-brand"
            />
            <StatTile
              label="最後回報"
              value={lastSeenIso ? formatRelative(lastSeenIso, now) : "—"}
              sub={lastSeenIso ? formatDateTime(lastSeenIso) : undefined}
              icon={<Clock className="h-4 w-4" />}
            />
          </div>

          {/* Trend charts */}
          <div className="grid gap-5 xl:grid-cols-2">
            <Card>
              <CardHeader
                title="溫度趨勢"
                subtitle={`最近 ${sorted.length} 筆讀值（°C）`}
              />
              <div className="px-2 pb-3 sm:px-3">
                {tempPoints.length > 1 ? (
                  <TelemetryChart
                    points={tempPoints}
                    metric="temperature"
                    unit="°C"
                    valueFormatter={(v) => formatTemp(v)}
                  />
                ) : (
                  <EmptyState message="資料點不足以繪製趨勢" className="py-16" />
                )}
              </div>
            </Card>

            <Card>
              <CardHeader
                title="壓力趨勢"
                subtitle={`最近 ${sorted.length} 筆讀值（bar）`}
              />
              <div className="px-2 pb-3 sm:px-3">
                {pressurePoints.length > 1 ? (
                  <TelemetryChart
                    points={pressurePoints}
                    metric="pressure"
                    unit="bar"
                    valueFormatter={(v) => formatPressure(v)}
                  />
                ) : (
                  <EmptyState message="資料點不足以繪製趨勢" className="py-16" />
                )}
              </div>
            </Card>
          </div>

          {/* Recent readings */}
          <Card>
            <CardHeader title="最新讀值" subtitle={`最近 ${Math.min(readings.length, 12)} 筆`} />
            <RecentReadings readings={readings.slice(0, 12)} />
          </Card>
        </>
      )}
    </div>
  );
}

function RecentReadings({
  readings,
}: {
  readings: Array<{
    id: string;
    temperature: number;
    pressure: number;
    status: string;
    timestamp: string;
  }>;
}) {
  if (readings.length === 0) {
    return <EmptyState message="尚無讀值" className="py-8" />;
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[420px] border-collapse text-sm">
        <thead>
          <tr className="border-y border-hairline text-left text-xs text-muted">
            <th className="py-2 pl-4 font-medium sm:pl-5">時間</th>
            <th className="px-3 py-2 text-right font-medium">溫度</th>
            <th className="px-3 py-2 text-right font-medium">壓力</th>
            <th className="px-3 py-2 font-medium">狀態</th>
          </tr>
        </thead>
        <tbody>
          {readings.map((r) => (
            <tr key={r.id} className="border-b border-hairline/60 last:border-0">
              <td className="tabular py-2.5 pl-4 text-muted sm:pl-5">{formatDateTime(r.timestamp)}</td>
              <td className="tabular px-3 py-2.5 text-right text-ink">{formatTemp(r.temperature)}</td>
              <td className="tabular px-3 py-2.5 text-right text-ink">{formatPressure(r.pressure)}</td>
              <td className="px-3 py-2.5">
                <StatusBadge status={r.status} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
