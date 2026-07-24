import { Pause, Play } from "lucide-react";
import { REFRESH_OPTIONS, useRefresh } from "../../live/RefreshProvider";
import { Segmented } from "../ui/Segmented";
import { cn } from "../../lib/cn";

/** Pause/resume polling and pick the cadence. Drives every query on the page. */
export function LiveControls() {
  const { paused, togglePaused, intervalMs, setIntervalMs } = useRefresh();

  return (
    <div className="flex items-center gap-2">
      <button
        type="button"
        onClick={togglePaused}
        aria-pressed={!paused}
        className={cn(
          "inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1.5 text-xs font-medium ring-1 transition-colors",
          paused
            ? "bg-surface-2 text-muted ring-hairline hover:text-ink"
            : "bg-good/10 text-good ring-good/20"
        )}
        title={paused ? "已暫停自動更新" : "自動更新中"}
      >
        {paused ? (
          <Play className="h-3.5 w-3.5" />
        ) : (
          <span className="relative inline-flex h-2 w-2" aria-hidden>
            <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-good/70" />
            <span className="relative inline-flex h-2 w-2 rounded-full bg-good" />
          </span>
        )}
        {paused ? "已暫停" : "即時"}
        {!paused && <Pause className="h-3.5 w-3.5 opacity-60" />}
      </button>

      <Segmented
        ariaLabel="更新頻率"
        options={REFRESH_OPTIONS.map((o) => ({ label: o.label, value: o.ms }))}
        value={intervalMs}
        onChange={setIntervalMs}
        className="hidden sm:inline-flex"
      />
    </div>
  );
}
