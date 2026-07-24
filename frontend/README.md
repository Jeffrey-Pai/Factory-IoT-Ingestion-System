# Factory IoT · 監控儀表板（前端）

即時顯示工廠 IoT 遙測資料的網頁儀表板。讀取後端 REST API,提供**廠區總覽**與**單機詳情**兩個畫面,支援亮/暗色主題與可調整的自動更新頻率。

## 技術選型

| 項目 | 選擇 | 理由 |
|------|------|------|
| UI 框架 | **React 18 + TypeScript** | 生態成熟、型別安全,長期好維護 |
| 建置工具 | **Vite 5** | 開發啟動快、建置快 |
| 樣式 | **Tailwind CSS**(+ CSS 變數 tokens) | 亮/暗色主題集中管理,樣式一致 |
| 資料抓取 | **TanStack Query** | 宣告式抓取、快取、自動輪詢與載入/錯誤狀態 |
| 圖表 | **Recharts** | 宣告式時間序列圖 |
| 路由 | **React Router** | 總覽 ↔ 詳情頁切換 |
| 圖示 | **lucide-react** | 輕量 SVG 圖示 |

## 畫面

- **廠區總覽 `/`** — KPI(回報機台數、總讀值、運轉正常率、需注意數)、讀值狀態分佈、可搜尋/排序的機台總覽表(含溫度/壓力區間條與即時/離線指示)。
- **單機詳情 `/machines/:machineId`** — 單機統計、溫度與壓力趨勢圖(游標提示)、最新讀值列表。

## 本機開發

需要 Node.js 20+(建議 22)。

```bash
cd frontend
npm install
npm run dev
```

開發伺服器預設在 http://localhost:5173,並會把 `/api` 與 `/health` 代理到後端(預設 `http://localhost:8080`)。因此瀏覽器只面對單一來源,**不需要在後端設定 CORS**。

若後端在其他位址,設定 `BACKEND_URL` 再啟動:

```bash
BACKEND_URL=http://localhost:5080 npm run dev
```

> 後端可用 `docker-compose up -d backend-api`(連同 rabbitmq、mssql、simulator)在本機跑起來。

## 建置與檢查

```bash
npm run build      # 型別檢查(tsc)+ 產出 dist/
npm run preview    # 本機預覽 dist/
npm run typecheck  # 只做型別檢查
```

## 以 Docker 執行(隨整套系統)

前端已加入根目錄的 `docker-compose.yml`。在專案根目錄:

```bash
docker-compose up -d --build
```

啟動後打開 **http://localhost:8081**。生產映像用 nginx 提供靜態檔並把 `/api`、`/health` 反向代理到 `backend-api:8080`(同源、免 CORS)。

## 對應的後端 API

| 畫面元素 | 端點 |
|----------|------|
| KPI、狀態分佈 | `GET /api/v1/fleet/status?windowMinutes=N` |
| 機台總覽表 | `GET /api/v1/machines` |
| 單機統計卡片 | `GET /api/v1/telemetry/{id}/stats?windowMinutes=N` |
| 溫度/壓力趨勢、最新讀值 | `GET /api/v1/telemetry/{id}/latest?count=N` |
| Worker 狀態晶片 | `GET /health/worker` |

型別定義見 `src/api/types.ts`,呼叫封裝在 `src/api/client.ts`,查詢 hooks 在 `src/api/queries.ts`。

## 專案結構

```
src/
├── api/          # 型別、fetch client、TanStack Query hooks
├── components/   # UI 元件(ui/ 為基礎元件)、圖表、表格
│   ├── ui/       # Card、StatTile、StatusBadge、Segmented…
│   └── layout/   # Header、AppShell、主題切換、即時控制
├── pages/        # FleetOverview、MachineDetail、NotFound
├── live/         # 自動更新頻率 context
├── theme/        # 亮/暗色主題 context
└── lib/          # 格式化、狀態對應、色盤、常數
```

## 設計說明

- 色盤採用經過色盲檢驗的資料視覺化配色:溫度用暖色(橘)、壓力用冷色(藍),各自獨立成圖(不用雙 Y 軸)。
- 運轉狀態使用固定的狀態色(綠=運轉、黃=警告…),且一律「色點 + 文字」呈現,不單靠顏色傳達。
- 主題色以 CSS 變數集中定義(`src/index.css`),圖表用的字面色值在 `src/lib/palette.ts`,兩者刻意保持同步。
