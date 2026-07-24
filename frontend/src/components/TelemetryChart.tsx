import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { useTheme } from "../theme/ThemeProvider";
import { paletteFor } from "../lib/palette";
import { formatClock } from "../lib/format";

export interface ChartPoint {
  /** Epoch millis — a numeric time axis keeps gaps proportional. */
  t: number;
  value: number;
}

interface TelemetryChartProps {
  points: ChartPoint[];
  metric: "temperature" | "pressure";
  unit: string;
  valueFormatter: (v: number) => string;
  yDomain?: [number | "auto", number | "auto"];
  height?: number;
}

/**
 * A single-series time-series (area + 2px line) with a crosshair tooltip. One hue
 * per chart — temperature and pressure live in separate charts (never a dual axis),
 * so each is unambiguous without a legend; the card title names the series.
 */
export function TelemetryChart({
  points,
  metric,
  unit,
  valueFormatter,
  yDomain = ["auto", "auto"],
  height = 240,
}: TelemetryChartProps) {
  const { theme } = useTheme();
  const p = paletteFor(theme);
  const color = metric === "temperature" ? p.temperature : p.pressure;
  const gradientId = `fill-${metric}`;

  return (
    <ResponsiveContainer width="100%" height={height}>
      <AreaChart data={points} margin={{ top: 8, right: 12, bottom: 0, left: 0 }}>
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={color} stopOpacity={0.26} />
            <stop offset="100%" stopColor={color} stopOpacity={0} />
          </linearGradient>
        </defs>
        <CartesianGrid stroke={p.grid} vertical={false} />
        <XAxis
          dataKey="t"
          type="number"
          scale="time"
          domain={["dataMin", "dataMax"]}
          tickFormatter={(t) => formatClock(t as number)}
          stroke={p.axis}
          tick={{ fill: p.axis, fontSize: 11 }}
          tickLine={false}
          axisLine={{ stroke: p.grid }}
          minTickGap={44}
        />
        <YAxis
          domain={yDomain}
          stroke={p.axis}
          tick={{ fill: p.axis, fontSize: 11 }}
          tickLine={false}
          axisLine={false}
          width={44}
        />
        <Tooltip
          cursor={{ stroke: p.axis, strokeDasharray: "3 3" }}
          content={<TelemetryTooltip unit={unit} formatter={valueFormatter} color={color} />}
        />
        <Area
          type="monotone"
          dataKey="value"
          stroke={color}
          strokeWidth={2}
          fill={`url(#${gradientId})`}
          dot={false}
          activeDot={{ r: 4, strokeWidth: 2, stroke: p.surface, fill: color }}
          isAnimationActive={false}
        />
      </AreaChart>
    </ResponsiveContainer>
  );
}

interface TooltipProps {
  active?: boolean;
  payload?: Array<{ payload: ChartPoint }>;
  unit: string;
  formatter: (v: number) => string;
  color: string;
}

function TelemetryTooltip({ active, payload, formatter, color }: TooltipProps) {
  if (!active || !payload?.length) return null;
  const point = payload[0].payload;
  return (
    <div className="rounded-lg bg-surface px-3 py-2 text-xs shadow-card ring-1 ring-hairline">
      <div className="tabular mb-1 text-muted">{formatClock(point.t)}</div>
      <div className="flex items-center gap-1.5">
        <span className="h-2 w-2 rounded-full" style={{ background: color }} aria-hidden />
        <span className="tabular font-medium text-ink">{formatter(point.value)}</span>
      </div>
    </div>
  );
}
