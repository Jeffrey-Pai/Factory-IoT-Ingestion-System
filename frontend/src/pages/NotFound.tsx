import { Link } from "react-router-dom";

export function NotFound() {
  return (
    <div className="flex flex-col items-center justify-center gap-3 py-24 text-center">
      <p className="text-5xl font-semibold text-ink">404</p>
      <p className="text-sm text-muted">找不到這個頁面</p>
      <Link
        to="/"
        className="mt-2 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-white transition-opacity hover:opacity-90"
      >
        回到總覽
      </Link>
    </div>
  );
}
