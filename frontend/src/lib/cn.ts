/** Tiny classname joiner — drops falsy values. Keeps JSX free of ternary noise. */
export function cn(...parts: Array<string | false | null | undefined>): string {
  return parts.filter(Boolean).join(" ");
}
