import { Moon, Sun } from "lucide-react";
import { useTheme } from "../../theme/ThemeProvider";

export function ThemeToggle() {
  const { theme, toggle } = useTheme();
  const dark = theme === "dark";
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={dark ? "切換到淺色模式" : "切換到深色模式"}
      title={dark ? "淺色模式" : "深色模式"}
      className="inline-flex h-8 w-8 items-center justify-center rounded-lg text-muted ring-1 ring-hairline transition-colors hover:bg-surface-2 hover:text-ink"
    >
      {dark ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
    </button>
  );
}
