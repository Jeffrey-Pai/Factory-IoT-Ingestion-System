import type { ReactNode } from "react";
import { Header } from "./Header";

export function AppShell({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-full">
      <Header />
      <main className="mx-auto max-w-7xl px-4 py-6 sm:px-6">{children}</main>
      <footer className="mx-auto max-w-7xl px-4 pb-10 pt-2 text-center text-xs text-faint sm:px-6">
        Factory IoT Ingestion System · 資料來自 REST API,依所選頻率自動更新
      </footer>
    </div>
  );
}
