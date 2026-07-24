import { Link } from "react-router-dom";
import { Activity } from "lucide-react";
import { ThemeToggle } from "./ThemeToggle";
import { LiveControls } from "./LiveControls";
import { WorkerHealthChip } from "./WorkerHealthChip";

export function Header() {
  return (
    <header className="sticky top-0 z-30 border-b border-hairline bg-canvas/85 backdrop-blur">
      <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-3 px-4 py-3 sm:px-6">
        <Link to="/" className="flex items-center gap-2.5" aria-label="回到總覽">
          <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-brand text-white shadow-sm">
            <Activity className="h-5 w-5" strokeWidth={2.4} />
          </span>
          <span>
            <span className="block text-sm font-semibold leading-tight text-ink">Factory IoT</span>
            <span className="block text-xs leading-tight text-muted">即時監控儀表板</span>
          </span>
        </Link>

        <div className="flex items-center gap-2">
          <WorkerHealthChip />
          <LiveControls />
          <ThemeToggle />
        </div>
      </div>
    </header>
  );
}
