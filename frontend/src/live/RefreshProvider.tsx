import {
  createContext,
  useContext,
  useMemo,
  useState,
  type ReactNode,
} from "react";

/** Selectable auto-refresh cadences for the live dashboard. */
export const REFRESH_OPTIONS = [
  { label: "2 秒", ms: 2_000 },
  { label: "5 秒", ms: 5_000 },
  { label: "10 秒", ms: 10_000 },
  { label: "30 秒", ms: 30_000 },
] as const;

interface RefreshContextValue {
  intervalMs: number;
  paused: boolean;
  /** What to hand TanStack Query's refetchInterval: the cadence, or false when paused. */
  refetchInterval: number | false;
  setIntervalMs: (ms: number) => void;
  togglePaused: () => void;
}

const RefreshContext = createContext<RefreshContextValue | null>(null);

export function RefreshProvider({ children }: { children: ReactNode }) {
  const [intervalMs, setIntervalMs] = useState<number>(5_000);
  const [paused, setPaused] = useState(false);

  const value = useMemo<RefreshContextValue>(
    () => ({
      intervalMs,
      paused,
      refetchInterval: paused ? false : intervalMs,
      setIntervalMs,
      togglePaused: () => setPaused((p) => !p),
    }),
    [intervalMs, paused]
  );

  return <RefreshContext.Provider value={value}>{children}</RefreshContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useRefresh(): RefreshContextValue {
  const ctx = useContext(RefreshContext);
  if (!ctx) throw new Error("useRefresh must be used within <RefreshProvider>");
  return ctx;
}
