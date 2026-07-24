import type { ReactNode } from "react";
import { Card } from "./Card";
import { cn } from "../../lib/cn";

interface StatTileProps {
  label: string;
  value: ReactNode;
  sub?: ReactNode;
  icon?: ReactNode;
  /** Tailwind text-colour class for the icon chip, e.g. "text-brand". */
  iconClassName?: string;
}

/** A single headline number: label, big value, optional sub-line and icon. */
export function StatTile({ label, value, sub, icon, iconClassName }: StatTileProps) {
  return (
    <Card className="p-4">
      <div className="flex items-start justify-between gap-2">
        <p className="text-xs font-medium text-muted">{label}</p>
        {icon && (
          <span className={cn("shrink-0 text-faint", iconClassName)} aria-hidden>
            {icon}
          </span>
        )}
      </div>
      <p className="tabular mt-2 text-2xl font-semibold leading-tight text-ink">{value}</p>
      {sub && <p className="mt-1 text-xs text-muted">{sub}</p>}
    </Card>
  );
}
