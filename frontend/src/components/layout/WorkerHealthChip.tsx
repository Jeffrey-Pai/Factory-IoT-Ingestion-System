import { useWorkerHealth } from "../../api/queries";
import { formatRelative } from "../../lib/format";
import { cn } from "../../lib/cn";

/**
 * Ingestion-worker liveness at a glance. The endpoint returns 503 when unhealthy,
 * which surfaces here as an error → "異常". A healthy worker shows when it last
 * consumed a message.
 */
export function WorkerHealthChip() {
  const { data, isLoading, isError } = useWorkerHealth();

  const state = isLoading
    ? { dot: "bg-faint", text: "text-muted", label: "檢查中", title: "正在檢查 Worker 狀態" }
    : isError || !data?.isHealthy
      ? { dot: "bg-warn", text: "text-ink", label: "Worker 異常", title: "資料採集 Worker 未回報健康狀態" }
      : {
          dot: "bg-good",
          text: "text-ink",
          label: "Worker 正常",
          title: data.lastMessageReceived
            ? `最後收到訊息：${formatRelative(data.lastMessageReceived)}`
            : "Worker 運作中",
        };

  return (
    <span
      title={state.title}
      className={cn(
        "hidden items-center gap-1.5 rounded-full bg-surface-2 px-2.5 py-1 text-xs font-medium ring-1 ring-hairline md:inline-flex",
        state.text
      )}
    >
      <span className={cn("h-2 w-2 rounded-full", state.dot)} aria-hidden />
      {state.label}
    </span>
  );
}
