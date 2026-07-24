import { cn } from "../../lib/cn";

/** A liveness dot: a soft ping halo when live, a flat muted dot when stale. */
export function LiveDot({ live, className }: { live: boolean; className?: string }) {
  return (
    <span className={cn("relative inline-flex h-2.5 w-2.5", className)} aria-hidden>
      {live && (
        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-good/60" />
      )}
      <span
        className={cn(
          "relative inline-flex h-2.5 w-2.5 rounded-full",
          live ? "bg-good" : "bg-faint"
        )}
      />
    </span>
  );
}
