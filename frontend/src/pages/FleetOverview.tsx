import { useState } from "react";
import { useFleetStatus, useMachines } from "../api/queries";
import { DEFAULT_WINDOW_MINUTES } from "../lib/constants";
import { Card, CardHeader } from "../components/ui/Card";
import { ErrorState, Loading } from "../components/ui/States";
import { WindowSelector } from "../components/WindowSelector";
import { FleetKpis } from "../components/FleetKpis";
import { StatusBreakdownBar } from "../components/StatusBreakdownBar";
import { MachineTable } from "../components/MachineTable";

export function FleetOverview() {
  const [windowMinutes, setWindowMinutes] = useState<number>(DEFAULT_WINDOW_MINUTES);
  const fleet = useFleetStatus(windowMinutes);
  const machines = useMachines();

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-lg font-semibold text-ink">廠區總覽</h1>
          <p className="text-sm text-muted">全廠機台即時健康狀態與遙測彙總</p>
        </div>
        <WindowSelector value={windowMinutes} onChange={setWindowMinutes} />
      </div>

      {/* KPI row */}
      {fleet.isLoading ? (
        <Card className="p-2">
          <Loading label="讀取廠區狀態…" />
        </Card>
      ) : fleet.isError ? (
        <Card className="p-2">
          <ErrorState
            message={(fleet.error as Error).message}
            onRetry={() => fleet.refetch()}
          />
        </Card>
      ) : (
        fleet.data && <FleetKpis status={fleet.data} />
      )}

      <div className="grid gap-5 lg:grid-cols-3">
        {/* Status breakdown */}
        <Card className="lg:col-span-1 lg:self-start">
          <CardHeader title="讀值狀態分佈" subtitle="依運轉狀態統計此範圍內的讀值" />
          <div className="px-4 pb-5 sm:px-5">
            {fleet.isLoading ? (
              <Loading label="統計中…" className="py-8" />
            ) : fleet.data ? (
              <StatusBreakdownBar breakdown={fleet.data.breakdown} />
            ) : null}
          </div>
        </Card>

        {/* Machine roster */}
        <Card className="lg:col-span-2">
          <CardHeader
            title="機台總覽"
            subtitle="點選機台查看詳細趨勢"
            right={
              machines.isFetching && !machines.isLoading ? (
                <span className="text-xs text-faint">更新中…</span>
              ) : undefined
            }
          />
          {machines.isLoading ? (
            <Loading label="載入機台清單…" />
          ) : machines.isError ? (
            <ErrorState
              message={(machines.error as Error).message}
              onRetry={() => machines.refetch()}
            />
          ) : (
            <MachineTable machines={machines.data ?? []} />
          )}
        </Card>
      </div>
    </div>
  );
}
