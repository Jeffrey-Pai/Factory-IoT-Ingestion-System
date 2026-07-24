import { lazy, Suspense } from "react";
import { Route, Routes } from "react-router-dom";
import { RefreshProvider } from "./live/RefreshProvider";
import { AppShell } from "./components/layout/AppShell";
import { FleetOverview } from "./pages/FleetOverview";
import { NotFound } from "./pages/NotFound";
import { Loading } from "./components/ui/States";

// The detail page pulls in Recharts (the bulk of the bundle); load it on demand so
// the landing overview stays lightweight.
const MachineDetail = lazy(() =>
  import("./pages/MachineDetail").then((m) => ({ default: m.MachineDetail }))
);

export default function App() {
  return (
    <RefreshProvider>
      <AppShell>
        <Suspense fallback={<Loading className="py-24" />}>
          <Routes>
            <Route path="/" element={<FleetOverview />} />
            <Route path="/machines/:machineId" element={<MachineDetail />} />
            <Route path="*" element={<NotFound />} />
          </Routes>
        </Suspense>
      </AppShell>
    </RefreshProvider>
  );
}
