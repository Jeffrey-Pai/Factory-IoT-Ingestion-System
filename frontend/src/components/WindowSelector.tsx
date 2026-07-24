import { Segmented } from "./ui/Segmented";
import { WINDOW_OPTIONS } from "../lib/constants";

/** Rolling time-window picker (15 分 / 1 小時 / 6 小時 / 24 小時). */
export function WindowSelector({
  value,
  onChange,
}: {
  value: number;
  onChange: (value: number) => void;
}) {
  return (
    <Segmented
      ariaLabel="時間範圍"
      options={WINDOW_OPTIONS.map((o) => ({ label: o.label, value: o.value }))}
      value={value}
      onChange={onChange}
    />
  );
}
