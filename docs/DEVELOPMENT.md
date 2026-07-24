# 開發者指南（Development Guide）

給要動程式碼的人：如何在本機建置、執行、除錯，以及常見的擴充任務（加 API、加 Migration、換資料庫）。

> 📐 先讀 [架構文件 ARCHITECTURE.md](./ARCHITECTURE.md) 了解分層與資料流會更順。

---

## 目錄

1. [環境需求](#1-環境需求)
2. [專案結構](#2-專案結構)
3. [建置與執行](#3-建置與執行)
4. [本機混合開發（推薦）](#4-本機混合開發推薦)
5. [資料庫 Migration](#5-資料庫-migration)
6. [常見擴充任務](#6-常見擴充任務)
7. [程式碼慣例](#7-程式碼慣例)
8. [除錯技巧](#8-除錯技巧)

---

## 1. 環境需求

| 工具 | 版本 | 用途 |
|------|------|------|
| .NET SDK | 8.0 | 建置與執行 |
| Docker & Docker Compose | 最新 | 跑相依服務（RabbitMQ / SQL Server 等） |
| EF Core Tools | `dotnet-ef` 8.x | 管理 Migration（見下） |
| IDE | VS 2022 / VS Code / Rider | 皆可 |

安裝 EF Core CLI 工具：

```bash
dotnet tool install --global dotnet-ef --version 8.*
```

---

## 2. 專案結構

```
Factory-IoT-Ingestion-System/
├─ FactoryIoT.slnx                    # 方案檔（新版 XML 格式）
├─ docker-compose.yml                 # 6 個服務的編排
├─ prometheus.yml                     # Prometheus 抓取設定
├─ rabbitmq.conf                      # 解除 guest loopback 限制
├─ k6-script.js                       # 壓測腳本
├─ docs/                              # ← 你在這裡
├─ src/
│  ├─ FactoryIoT.Domain/             # 實體 + 儲存庫介面（無外部相依）
│  │  ├─ Entities/                   #   Telemetry, SensorReading
│  │  ├─ Analytics/                  #   MachineTelemetrySummary, TelemetryStatistics, FleetStatus（分析 read-model）
│  │  └─ Interfaces/                 #   ITelemetryRepository, ...
│  ├─ FactoryIoT.Application/        # DTO + 應用介面（相依 Domain）
│  │  ├─ DTOs/
│  │  └─ Common/Interfaces/          #   ITelemetryConsumer, IMessagePublisher
│  ├─ FactoryIoT.Infrastructure/     # 外部技術實作（相依 Domain + Application）
│  │  ├─ Messaging/                  #   RabbitMQ Consumer / Publisher / Config
│  │  ├─ Workers/                    #   TelemetryIngestionWorker（核心）
│  │  ├─ Persistence/                #   DbContext / Repository
│  │  └─ Migrations/                 #   EF Core 遷移
│  ├─ FactoryIoT.Presentation/       # Web API 入口（相依 Application + Infrastructure）
│  │  ├─ Program.cs                  #   Minimal API + DI 組裝
│  │  └─ Dockerfile
│  └─ FactoryIoT.Simulator/          # 資料產生器 Console（相依 Domain）
│     ├─ Program.cs
│     └─ Dockerfile
└─ tests/
   └─ FactoryIoT.Tests/              # xUnit：Domain 純邏輯 + Repository（EF Core InMemory）
```

**依賴方向鐵律**：`Presentation → Infrastructure → Application → Domain`。Domain 不可 `using` 任何外層或第三方套件。加程式碼前先想清楚「這段該放哪一層」。

---

## 3. 建置與執行

### 全部用 Docker（最省事）

```bash
docker compose up -d --build
```

### 純用 .NET CLI 建置

```bash
dotnet restore FactoryIoT.slnx
dotnet build FactoryIoT.slnx
```

### 執行測試

單元測試放在 `tests/FactoryIoT.Tests`（xUnit）。測試**不需要** Docker、RabbitMQ 或 SQL Server — Repository 的分析查詢是跑在 EF Core 的 **InMemory** provider 上：

```bash
dotnet test FactoryIoT.slnx
```

涵蓋範圍：
- **Domain 純邏輯** — `SensorReading.FromTelemetry` 的拆解與參數防呆。
- **Repository 分析查詢** — `GetMachineSummariesAsync`／`GetStatisticsAsync`（含時間窗過濾與查無資料回 `null`）／`GetFleetStatusAsync`（機台計數與狀態分佈），以及既有的 `GetLatestByMachineAsync`。

---

## 4. 本機混合開發（推薦）

開發時最舒服的做法：**相依服務用 Docker 跑，要改的程式（API / Simulator）在本機直接 `dotnet run`**，這樣改完立即重跑、可下中斷點。

### 步驟

1. 只啟動基礎設施：

   ```bash
   docker compose up -d rabbitmq mssql prometheus grafana
   ```

2. 在本機跑 Backend API（開發環境會開啟 Swagger）：

   ```bash
   cd src/FactoryIoT.Presentation
   dotnet run
   # → http://localhost:5297/swagger
   ```

   > 本機執行時會讀 `appsettings.json`，其連線目標是 `localhost`（`Server=localhost,1433` 與 `RabbitMQ:Host=localhost`），正好對應 Docker 對外映射的埠，無需額外設定。

3. 在另一個終端機跑模擬器：

   ```bash
   cd src/FactoryIoT.Simulator
   dotnet run
   # 預設連 localhost:5672，可用環境變數覆寫：
   # RABBITMQ_HOST=localhost dotnet run
   ```

### 設定覆寫優先序

| 來源 | 優先度 | 範例 |
|------|--------|------|
| 環境變數 `RABBITMQ_*` / `ConnectionStrings__Default` | 最高 | docker-compose 用這個覆寫 |
| `appsettings.{Environment}.json` | 中 | `appsettings.Development.json` |
| `appsettings.json` | 低 | 預設值（`localhost`） |

> RabbitMQ 連線特別注意：`Program.cs` 的 `EnvOrConfig` **先讀單底線環境變數 `RABBITMQ_HOST`，再讀 `RabbitMQ:Host` 設定**。詳見架構文件第 10 節的說明。

---

## 5. 資料庫 Migration

Migration 專案在 `FactoryIoT.Infrastructure`，但 `DbContext` 的設定入口在 `FactoryIoT.Presentation`（`Program.cs` 註冊 `AddDbContext`）。因此 EF 指令要用 `--startup-project` 指到 Presentation。

### 新增一個 Migration

改完實體（`Domain/Entities/*.cs`）或 `FactoryIoTDbContext` 的 `OnModelCreating` 之後：

```bash
dotnet ef migrations add <MigrationName> \
  --project src/FactoryIoT.Infrastructure \
  --startup-project src/FactoryIoT.Presentation
```

### 套用 Migration

正常情況下**不用手動套用** — `Program.cs` 在啟動時會自動 `await dbContext.Database.MigrateAsync()`。若要手動套用：

```bash
dotnet ef database update \
  --project src/FactoryIoT.Infrastructure \
  --startup-project src/FactoryIoT.Presentation
```

> ⚠️ 手動下指令需要 SQL Server 可連線（先 `docker compose up -d mssql`），且連線字串會取自 `appsettings.json`（`localhost,1433`）。

---

## 6. 常見擴充任務

### 6.1 新增一個 API 端點

在 `src/FactoryIoT.Presentation/Program.cs` 用 Minimal API 風格新增（依樣畫葫蘆）：

```csharp
app.MapGet("/api/v1/telemetry/{machineId}/health-score", async (
    string machineId,
    ITelemetryRepository repository) =>
{
    var latest = await repository.GetLatestByMachineAsync(machineId, 100);
    if (latest.Count == 0) return Results.NotFound();
    return Results.Ok(new
    {
        machineId,
        avgTemperature = latest.Average(t => t.Temperature),
        maxPressure = latest.Max(t => t.Pressure),
    });
})
.WithName("GetMachineHealthScore")
.WithOpenApi();
```

若需要新的查詢能力，先在 `Domain/Interfaces/ITelemetryRepository.cs` 加方法，再到 `Infrastructure/Persistence/TelemetryRepository.cs` 實作（依賴反轉原則）。

> 💡 **真實範例**：機台總覽／單機統計／全廠健康快照這三個分析端點（`/api/v1/machines`、`/api/v1/telemetry/{machineId}/stats`、`/api/v1/fleet/status`）就是照這個流程做出來的 —— 介面加在 `ITelemetryRepository`、實作用 EF Core `GroupBy` 在 **DB 端**聚合、回傳 `Domain/Analytics` 下的 read-model record。它們跟上面「撈 100 筆再於記憶體算」的示範不同，是把聚合下推到 SQL Server，資料量大時效率差很多。可直接讀這三個當範本。

### 6.2 新增一個遙測欄位

1. 在 `Domain/Entities/Telemetry.cs` 加屬性。
2. 若需要約束/索引，改 `FactoryIoTDbContext.OnModelCreating`。
3. `dotnet ef migrations add AddXxxField ...`（見上）。
4. 同步更新 `Simulator/Program.cs` 產生該欄位的值。

### 6.3 調整吞吐量參數

批次大小、間隔、prefetch、重試次數等都集中在 `TelemetryIngestionWorker` 的常數與 `RabbitMqTelemetryConsumer.PrefetchCount`。參數清單見 [操作手冊第 11 節](./OPERATIONS.md#11-設定參數速查)。

### 6.4 SensorReading 路徑（已接上）

`SensorReading` 現在是正規化的每感測器時序儲存，**已在執行路徑上**：

- Worker 在 `FlushBatchAsync` 中呼叫 `SensorReading.FromTelemetry(telemetry)`，把每筆寬表 `Telemetry` 拆成多筆讀值，與 `Telemetry` 在**同一交易**裡寫入 `SensorReadings` 表（`SensorReadingRepository`，於 `Program.cs` 以 `Scoped` 註冊）。
- 對外端點：`GET /api/v1/sensors/{machineId}/readings?sensorType=<Temperature|Pressure>&count=<1-100>`，回應型別 `SensorReadingDto`。

要新增感測器類型時，只要在 `SensorReading.FromTelemetry` 多產生一筆讀值即可，**免改資料庫 schema**（narrow/EAV 形態的好處）。

> 仍未接上的骨架：`RabbitMqPublisher`（`iot.readings` fanout）／`IMessagePublisher` 這條獨立發布管線未在 DI 註冊。詳見 [架構文件第 11 節](./ARCHITECTURE.md#11-架構備註實際運作-vs-骨架程式碼)。

---

## 7. 程式碼慣例

觀察現有程式碼，維持一致風格：

- **檔案作用域命名空間**（`namespace Foo;`）與**啟用 Nullable / ImplicitUsings**（見各 `.csproj`）。
- 實體用 `sealed` + `init` 唯讀屬性，傾向不可變（immutable）。
- 非同步方法一律 `async/await` 並傳遞 `CancellationToken`。
- 訊息類物件（Consumer / Publisher）用 **private 建構子 + static `CreateAsync` 工廠方法**（因為連線建立是非同步的）。
- 重要的「為什麼這樣寫」用註解留在程式碼裡（例如 `Program.cs` 的環境變數優先序、`rabbitmq.conf` 的 loopback 說明）— 沿用這個習慣。
- 日誌用結構化參數（`{MachineId}`）而非字串插值。

---

## 8. 除錯技巧

| 想做的事 | 怎麼做 |
|----------|--------|
| 看每一筆訊息內容 | 把 `appsettings.json` 的 `Logging:LogLevel:Default` 設為 `Debug`，重跑 |
| 確認 Worker 活著 | `curl http://localhost:8080/health/worker` |
| 看即時吞吐 | `curl -s http://localhost:8080/metrics \| grep telemetry` |
| 看佇列積壓 | RabbitMQ 管理 UI → Queues → `telemetry-queue` |
| 直接查資料庫 | 見 [操作手冊第 8 節](./OPERATIONS.md#8-查詢資料庫) |
| API 在本機下中斷點 | 用 [混合開發](#4-本機混合開發推薦) 方式 `dotnet run`，IDE 附加偵錯 |
| 驗證 API 契約 | 開發環境開 Swagger：http://localhost:8080/swagger |

### 常見卡關

- **本機跑 API 卻連不到 DB/MQ** → 確認基礎服務容器有起來（`docker compose ps`），且本機 `appsettings.json` 指向 `localhost`。
- **改了程式但 Docker 沒生效** → 要加 `--build`：`docker compose up -d --build backend-api`。
- **k6 壓測查無資料** → 腳本的 `machineIds` 已對齊 `EQP-001` ~ `EQP-050`；若你改了模擬器的機台命名，記得同步腳本。詳見操作手冊第 7 節。

---

## 延伸閱讀

- 📐 [架構文件 ARCHITECTURE.md](./ARCHITECTURE.md)
- 🛠️ [操作手冊 OPERATIONS.md](./OPERATIONS.md)
- 📄 [根目錄 README.md](../README.md)
