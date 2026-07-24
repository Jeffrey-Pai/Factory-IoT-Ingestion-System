import type { ReactNode } from "react";
import { AlertTriangle, Inbox, Loader2 } from "lucide-react";
import { cn } from "../../lib/cn";

export function Loading({ label = "載入中…", className }: { label?: string; className?: string }) {
  return (
    <div
      className={cn(
        "flex flex-col items-center justify-center gap-2 py-12 text-muted",
        className
      )}
    >
      <Loader2 className="h-5 w-5 animate-spin" />
      <p className="text-sm">{label}</p>
    </div>
  );
}

export function ErrorState({
  message,
  onRetry,
  className,
}: {
  message?: string;
  onRetry?: () => void;
  className?: string;
}) {
  return (
    <div
      className={cn("flex flex-col items-center justify-center gap-2 py-12 text-center", className)}
    >
      <AlertTriangle className="h-6 w-6 text-critical" />
      <p className="text-sm font-medium text-ink">載入失敗</p>
      {message && <p className="max-w-sm text-xs text-muted">{message}</p>}
      {onRetry && (
        <button
          type="button"
          onClick={onRetry}
          className="mt-1 rounded-md bg-surface-2 px-3 py-1.5 text-xs font-medium text-ink ring-1 ring-hairline transition-colors hover:bg-hairline/40"
        >
          重試
        </button>
      )}
    </div>
  );
}

export function EmptyState({
  message = "目前沒有資料",
  icon,
  className,
}: {
  message?: string;
  icon?: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cn("flex flex-col items-center justify-center gap-2 py-12 text-center", className)}
    >
      <span className="text-faint">{icon ?? <Inbox className="h-6 w-6" />}</span>
      <p className="text-sm text-muted">{message}</p>
    </div>
  );
}
