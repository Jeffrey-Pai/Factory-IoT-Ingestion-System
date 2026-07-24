/** Formatting helpers — one place for every number/date the UI renders. */

const intFmt = new Intl.NumberFormat("zh-Hant", { maximumFractionDigits: 0 });

export function formatInt(value: number): string {
  return intFmt.format(value);
}

export function formatNumber(value: number, digits = 1): string {
  return value.toLocaleString("zh-Hant", {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
}

export function formatTemp(value: number, digits = 1): string {
  return `${formatNumber(value, digits)}°C`;
}

export function formatPressure(value: number, digits = 2): string {
  return `${formatNumber(value, digits)} bar`;
}

/** Local wall-clock time, e.g. "14:03:07" — used on time-series axes and rows.
 *  Accepts an ISO string or epoch-millis (Recharts passes numeric axis values). */
export function formatClock(value: string | number): string {
  return new Date(value).toLocaleTimeString("zh-Hant", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

export function formatDateTime(value: string | number): string {
  return new Date(value).toLocaleString("zh-Hant", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

/** Compact Chinese relative time, e.g. "剛剛", "12 秒前", "3 分前". */
export function formatRelative(iso: string, now: number = Date.now()): string {
  const deltaMs = now - new Date(iso).getTime();
  const sec = Math.round(deltaMs / 1000);
  if (sec < 0) return "剛剛";
  if (sec < 3) return "剛剛";
  if (sec < 60) return `${sec} 秒前`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min} 分前`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr} 小時前`;
  const day = Math.floor(hr / 24);
  return `${day} 天前`;
}
