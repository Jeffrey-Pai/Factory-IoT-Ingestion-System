import { useEffect, useState } from "react";

/**
 * A `Date.now()` that advances on its own.
 *
 * Anything showing an age — "12 秒前", a liveness dot — is a function of two
 * things, and only one of them arrives with a poll. Reading the clock during
 * render ties it to the data instead: the display freezes whenever refetching
 * pauses or a request hangs, and a machine that went quiet ten minutes ago
 * keeps its green dot because nothing re-rendered to notice. Ticking
 * independently means "no new data" is itself visible.
 *
 * One second, because the labels have second resolution below a minute.
 */
export function useNow(intervalMs = 1_000): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);

  return now;
}
