# 讀取快取（Caching）— 為什麼導入 Redis、用在哪、怎麼運維

這份文件回答一個具體問題:**這個系統需要 Redis 嗎?** 答案是「需要,但只用在一個地方」。以下記錄判斷的依據、實際的設計,以及設定與運維方式。

> 一句話:**用一層短 TTL 的分散式讀取快取,擋掉儀表板/壓測/Prometheus 對「同一個聚合答案」的重複查詢,保護受限的 SQL Server Express。** 寫入路徑一律不碰。

---

## 1. 判斷:哪裡適合、哪裡不適合

導入一個基礎設施元件之前,先誠實地問「它解決的是這個系統真的有的問題嗎」。

### ✅ 適合 —— 分析讀取端(analytics read path)

四個唯讀端點 —— `/api/v1/machines`、`/api/v1/fleet/status`、`/api/v1/telemetry/{id}/stats`、`/api/v1/telemetry/{id}/latest` —— 同時具備教科書等級的「該快取」特徵:

| 特徵 | 這個系統的實際情況 |
|------|--------------------|
| **讀取放大(read amplification)** | 前端所有客戶端**同步輪詢**(預設 5 秒、最快 2 秒,見 `RefreshProvider`),k6 以 100 req/s 壓測,Prometheus 每 15 秒抓取 —— 而每個呼叫算出來的答案**對所有人完全相同**。`/machines` 尤其貴:每次都做 roster 讀取 + 未聚合尾段 `GROUP BY` + 記憶體合併。 |
| **資料庫受限** | SQL Server **Express**:1 GB buffer pool、≤4 核、10 GB 資料庫上限。整個 [分層儲存策略](./DATA-LIFECYCLE.md) 就是為了遷就它。用快取把重複的聚合查詢擋在 DB 之外,正是保護這個受限資源的手段。 |
| **本來就容忍些微延遲** | 這條路徑**設計上就是最終一致**:roster 的 `LastSeen` 結構性落後牆鐘 2–3 分鐘(見 [ARCHITECTURE §7](./ARCHITECTURE.md#7-資料模型database-schema)),視窗統計只精確到最後一個封閉的桶。再疊一層 1–5 秒的 TTL,只是在既有的延遲上加一個捨入誤差,而且遠小於前端判定機台存活用的 30 秒門檻(`INGESTION_SLACK_MS`)。 |
| **可水平擴充** | 系統全容器化。若讀取 API 擴充成多副本,`IMemoryCache`(每副本各一份)會造成 N 倍 miss、N 倍記憶體、各副本畫面不一致。**共享的** Redis 一份快取讓先算出結果的副本服務所有副本,才是對的選擇。 |

### ❌ 不適合 —— 這些地方刻意不碰

| 地方 | 為什麼不用 Redis |
|------|------------------|
| **當主資料庫 / 系統真相來源** | SQL Server + 預聚合分層才是系統真相。Redis 這裡是純快取,資料隨時可從 DB 重建。 |
| **高吞吐寫入路徑** | 攝取是 `RabbitMQ → Channel → 批次 INSERT`,已是對延遲最佳化的設計。塞 Redis 進去只會多一個相依與故障點,毫無好處。 |
| **取代 RabbitMQ 當緩衝** | RabbitMQ(持久化佇列 + 手動 ack + 背壓)已經扮演緩衝。用 Redis Streams 重寫是平行搬移,沒有效益。 |
| **即時推播(pub/sub → WebSocket)** | 「把輪詢換成伺服器推播」是一個真的功能,但那是更大的一次改動(需要 SignalR + 前端改寫),屬於未來事項(見 §6),不是「該不該有快取」的問題。 |

---

## 2. 設計

### 2.1 cache-aside 裝飾器,端點零改動

快取以**裝飾器(decorator)** 的形式包住 `ITelemetryRepository`,而不是散落在各端點裡:

```
Program.cs 端點  →  ITelemetryRepository
                     └── CachingTelemetryRepository（讀取走快取、寫入直通）
                          └── TelemetryRepository（EF Core → SQL Server）
```

- 端點、Ingestion Worker、其他所有程式都**繼續依賴純 `ITelemetryRepository`**,完全不知道快取存在 —— 這正是 Clean Architecture 依賴反轉的用法(和 `ITelemetryConsumer` 對 RabbitMQ 無感是同一招)。
- 抽象埠 `IAnalyticsCache` 定義在 **Application** 層,技術中立;Redis 實作 `RedisAnalyticsCache` 放在 **Infrastructure** 層。內層對 Redis 一無所知。
- **寫入 `AddRangeAsync` 一律直通、不失效(no invalidation)。** 新鮮度由 TTL 界定,而非由失效邏輯維護 —— 對本來就最終一致的資料而言,這樣最單純,也沒有失效邏輯可以寫錯。

### 2.2 兩個硬性保證

`RedisAnalyticsCache` 必須遵守兩件事,讀取路徑才敢依賴它:

1. **Fail-open(失敗時退回來源)。** 快取是最佳化,永遠不是正確性的相依。對 store 的每一次存取都被包起來:Redis 連不上或報錯時,仍然執行 factory(即查 DB)並回傳結果 —— 快取故障只會讓讀取**變慢**,絕不讓它**失敗**。(唯一例外:請求被取消時,`OperationCanceledException` 照常往上拋。)
2. **可快取負結果。** 「這台機台在這個視窗沒有任何回報」(404,`stats` 回 `null`)是有意義的答案。快取把「key 不存在(miss)」和「key 存在但值是 null(cached negative)」分開,所以一台安靜的機台不會每次輪詢都重算一次聚合。

### 2.3 key 與 TTL

| 區(region) | key | 預設 TTL | 說明 |
|-------------|-----|----------|------|
| `roster` | `all` | 2s | 全廠一份。TTL 短,因為 `LastSeen` 是存活訊號。 |
| `fleet` | `{windowMinutes}` | 2s | 依視窗寬度分鍵。 |
| `stats` | `{machineId}:{windowMinutes}` | 5s | 依機台 + 視窗寬度分鍵。 |
| `latest` | `{machineId}:{count}` | 1s | k6 壓測的目標端點。1 秒仍比前端最快的 2 秒輪詢新鮮。設 `0` 可只關掉這一個。 |

Redis 內實際 key 會冠上 `factoryiot:` 前綴(`InstanceName`),方便 `KEYS factoryiot:*` 檢視。

**視窗端點的分鍵關鍵**:端點把「最近 N 分鐘」翻成 `UtcNow - N 分鐘` 這個**一直在動**的時間戳交給 repository。若直接拿它當 key,每次輪詢都是新 key、命中率永遠 0。裝飾器因此把它**還原回穩定的視窗寬度**(`round(UtcNow - from)`,四捨五入到分鐘),讓所有問「最近一小時」的呼叫共用同一個 key。共用的值最多是一個 TTL 之前算的 —— 對以分鐘為單位的視窗來說,幾秒的誤差可忽略。

### 2.4 可觀測性

`RedisAnalyticsCache` 暴露一個 Prometheus 計數器,和專案其他指標一致:

```
analytics_cache_requests_total{region, outcome}   # outcome = hit | miss | error
```

Grafana 命中率:

```promql
sum(rate(analytics_cache_requests_total{outcome="hit"}[5m]))
  / sum(rate(analytics_cache_requests_total[5m]))
```

`outcome="error"` 持續 > 0 代表 Redis 有問題 —— 但因為 fail-open,使用者只會覺得變慢,不會看到錯誤。

---

## 3. 設定

綁定自 `appsettings.json` 的 `Cache` 區段,可用 ASP.NET 慣例的**雙底線** `Cache__*` 環境變數覆寫(docker-compose 就是這樣做)。

> ⚠️ 這裡刻意用雙底線 `Cache__RedisConnection`(idiomatic),**不同於**上游 `RABBITMQ_*` 那組單底線的歷史包袱(見 [ARCHITECTURE §10 的踩雷紀錄](./ARCHITECTURE.md#️-一個曾經踩過的雷rabbitmq-主機解析))。這裡沒有要自己手刻綁定。

| 設定鍵 | 預設 | 意義 |
|--------|------|------|
| `Cache:Enabled` | `true` | 總開關。可在有 Redis 的情況下臨時關掉快取(例如 A/B 一次 k6)。 |
| `Cache:RedisConnection` | `""`(空) | StackExchange.Redis 連線字串。**空字串 = 沒設定 Redis = 不快取**(直接查 DB)。`appsettings.json` 故意留空,所以本機 `dotnet run` 不需要 Redis 也能跑。 |
| `Cache:InstanceName` | `factoryiot:` | Redis key 前綴。 |
| `Cache:RosterTtlSeconds` | `2` | 各家族 TTL(秒)。任一設 `0` 即只關掉那一個家族。 |
| `Cache:FleetStatusTtlSeconds` | `2` | |
| `Cache:StatsTtlSeconds` | `5` | |
| `Cache:LatestTtlSeconds` | `1` | |

**啟用與否的判定**:只有在 `Enabled=true` **且** `RedisConnection` 非空時,才會接上 Redis 與裝飾器;否則讀取路徑原封不動直接走 DB,連 Redis 相依都不掛。

---

## 4. 運維

### 啟動

`docker-compose up -d` 會一併啟動 `redis` 服務(`redis:7.4-alpine`)。它以**純快取**姿態運行:

- `--save "" --appendonly no`:關閉持久化。這裡沒有系統真相資料,填滿或重啟後,下次 miss 自然從 SQL Server 重新填充。
- `--maxmemory 256mb --maxmemory-policy allkeys-lru`:記憶體上限 + LRU 淘汰,永遠不會把主機吃爆。

backend-api 透過 `Cache__RedisConnection: redis:6379` 指向它,並 `depends_on` 它 healthy。

### 檢查

```bash
# 命中率(需先在 Grafana 加 Prometheus data source)
#   sum(rate(analytics_cache_requests_total{outcome="hit"}[5m])) / sum(rate(analytics_cache_requests_total[5m]))

# 直接看 Redis 裡有什麼
docker exec -it redis redis-cli KEYS 'factoryiot:*'
docker exec -it redis redis-cli GET 'factoryiot:roster:all'

# 清掉快取(下次請求會重算)
docker exec -it redis redis-cli FLUSHALL
```

### 驗證快取有效(A/B)

```bash
# 1) 關掉快取跑一次 k6,記下 p95
docker compose exec backend-api sh -c 'echo cache off' # 或設 Cache__Enabled=false 重啟
k6 run k6-script.js
# 2) 開快取再跑一次,比較 p95 與 SQL Server CPU
```

### Redis 掛掉會怎樣?

**什麼都不會壞。** fail-open 保證讀取退回直接查 DB,只是變慢、SQL Server 負載變高 —— 就跟從來沒加快取一樣。`analytics_cache_requests_total{outcome="error"}` 會上升,可據此告警。

---

## 5. 影響範圍

- **API 契約不變**:同樣的端點、同樣的回應格式。前端**零改動**。
- **寫入路徑不變**:Ingestion Worker 的批次寫入交易完全不受影響(裝飾器把 `AddRangeAsync` 直通,且與 `SensorReadingRepository` 共用同一個 scoped `DbContext`,交易原子性照舊)。
- **無 Redis 也能跑**:本機開發不需要 Redis。

---

## 6. 未來延伸(本次未做)

- **即時推播**:用 Redis pub/sub 或 RabbitMQ fanout + SignalR 把儀表板從輪詢改成伺服器推播,進一步降低讀取量。
- **API 速率限制**:Redis 後端的分散式 rate limiting(目前是內部系統,優先度低)。
- **`SensorReadings` 讀取快取**:`/api/v1/sensors/{id}/readings` 也可納入同一套 `IAnalyticsCache`,做法與本文相同。

---

## 延伸閱讀

- 📐 [系統架構 ARCHITECTURE.md](./ARCHITECTURE.md) — 分層設計與技術決策
- 🗄️ [資料生命週期 DATA-LIFECYCLE.md](./DATA-LIFECYCLE.md) — 為什麼 DB 受限、分層儲存怎麼運作
- 🛠️ [操作手冊 OPERATIONS.md](./OPERATIONS.md) — 啟動、驗證、壓測
