import { statusMeta } from "../../lib/status";
import { cn } from "../../lib/cn";
import { toneDot } from "./tone";

/**
 * A status pill: neutral surface + a coloured dot + an ink label. Identity is
 * carried by the dot *and* the text (never colour alone), which keeps it readable
 * even where the raw status hue is low-contrast on the surface.
 */
export function StatusBadge({ status, className }: { status: string; className?: string }) {
  const { tone, label } = statusMeta(status);
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full bg-surface-2 px-2.5 py-0.5",
        "text-xs font-medium text-ink ring-1 ring-hairline",
        className
      )}
    >
      <span className={cn("h-2 w-2 rounded-full", toneDot[tone])} aria-hidden />
      {label}
    </span>
  );
}
