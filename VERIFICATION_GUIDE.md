# RabbitMQ到MSSQL數據流驗證指南

## 問題診斷與修復總結

### 原始問題
設備模擬器發送的訊息進入RabbitMQ後，無法被消化（consumed）並存入MSSQL資料庫。

### 已實施的改進

#### 1. 增強日誌記錄
- **TelemetryIngestionWorker**: 新增詳細的日誌追蹤
  - 資料庫連線驗證日誌
  - RabbitMQ連線狀態日誌  
  - 批次處理進度日誌（每100條訊息）
  - 資料庫寫入成功/失敗的明確標記（✓/✗）
  - 關鍵錯誤的CRITICAL級別警告

- **RabbitMqTelemetryConsumer**: 新增訊息消費追蹤
  - 訊息接收計數（每100條記錄一次）
  - 訊息處理詳細資訊（Debug級別）
  - 連線建立過程日誌

#### 2. 健康檢查端點
新增 `/health/worker` 端點，提供以下資訊：
- Worker是否健康（isHealthy）
- 最後一條訊息接收時間（lastMessageReceived）
- 最後一次資料庫寫入時間（lastBatchFlushed）
- 距離最後一條訊息的時間（timeSinceLastMessage）
- 距離最後一次寫入的時間（timeSinceLastFlush）

#### 3. Prometheus監控指標
新增以下指標：
- `telemetry_consumed_total`: 從RabbitMQ消費的訊息總數
- `telemetry_written_total`: 成功寫入資料庫的記錄總數
- `telemetry_failed_total`: 寫入失敗的記錄總數
- `telemetry_batch_processing_seconds`: 批次處理耗時

#### 4. 資料庫連線驗證
在Worker啟動時驗證資料庫連線，提前發現問題。

## 驗證步驟

### 1. 啟動服務
```bash
docker-compose up -d
```

### 2. 檢查服務狀態
```bash
# 檢查所有服務是否正常運行
docker-compose ps

# 查看backend-api日誌
docker-compose logs -f backend-api

# 查看simulator日誌
docker-compose logs -f simulator
```

### 3. 驗證Worker狀態
```bash
# 檢查健康狀態
curl http://localhost:8080/health/worker
```

預期輸出應包含：
```json
{
  "isHealthy": true,
  "lastMessageReceived": "2026-07-22T09:00:00Z",
  "lastBatchFlushed": "2026-07-22T09:00:02Z",
  "timeSinceLastMessage": "00:00:01.5",
  "timeSinceLastFlush": "00:00:03.2"
}
```

### 4. 檢查Prometheus指標
```bash
curl http://localhost:8080/metrics | grep telemetry
```

應該看到遞增的計數器：
- `telemetry_consumed_total`：RabbitMQ消費的訊息數
- `telemetry_written_total`：寫入資料庫的記錄數

### 5. 查詢資料庫
使用SQL Server Management Studio (SSMS) 連線到資料庫：
- **伺服器**: localhost,1433
- **使用者**: sa
- **密碼**: IoT_Secret123!
- **資料庫**: factory_iot

執行查詢：
```sql
-- 檢查總記錄數
SELECT COUNT(*) FROM Telemetries;

-- 檢查最近的記錄
SELECT TOP 10 * FROM Telemetries ORDER BY Timestamp DESC;

-- 檢查每台機器的記錄數
SELECT MachineId, COUNT(*) as Count 
FROM Telemetries 
GROUP BY MachineId 
ORDER BY MachineId;
```

### 6. 驗證RabbitMQ
訪問 RabbitMQ Management UI: http://localhost:15672
- 使用者名稱: `guest`
- 密碼: `guest`

檢查 `telemetry-queue` 佇列：
- **Ready**: 應該接近0（訊息被快速消費）
- **Unacked**: 正在處理的訊息
- **Total**: 應該持續增加然後減少

## 日誌分析

### 正常運行的日誌標記
```
✓ Successfully connected to RabbitMQ
✓ RabbitMQ consumer is now actively listening
✓ Processed 100 messages from RabbitMQ
✓ Successfully saved 100 telemetry records to MSSQL
```

### 問題標記
```
✗ Failed to save batch
✗✗✗ CRITICAL: Failed to save batch after 3 attempts. DATA LOSS
```

## 故障排除

### 問題1: Worker無法連線到RabbitMQ
**症狀**: 日誌顯示 "Failed to connect to RabbitMQ"

**解決方案**:
1. 檢查RabbitMQ容器是否運行: `docker-compose ps rabbitmq`
2. 檢查網路連線: `docker-compose logs rabbitmq`
3. 確認環境變數 `RABBITMQ_HOST=rabbitmq` 已設定

### 問題2: 訊息被消費但未寫入資料庫
**症狀**: `telemetry_consumed_total` 增加，但 `telemetry_written_total` 不變

**解決方案**:
1. 檢查日誌中的資料庫錯誤訊息
2. 驗證連線字串：檢查密碼、伺服器位址
3. 確認MSSQL容器運行正常: `docker-compose ps mssql`
4. 檢查資料庫遷移是否成功執行

### 問題3: 訊息堆積在RabbitMQ
**症狀**: RabbitMQ中 Ready 訊息數持續增加

**解決方案**:
1. 檢查backend-api容器是否運行
2. 查看Worker日誌確認是否正常消費
3. 檢查資料庫效能是否成為瓶頸

## 效能監控

使用Grafana建立儀表板（http://localhost:3000）：

1. 新增Prometheus數據源（http://prometheus:9090）

2. 建立以下圖表：
   - 訊息消費速率: `rate(telemetry_consumed_total[1m])`
   - 資料庫寫入速率: `rate(telemetry_written_total[1m])`
   - 失敗率: `rate(telemetry_failed_total[1m])`
   - 批次處理延遲: `telemetry_batch_processing_seconds`

## 下一步建議

如果問題仍然存在：

1. **啟用Debug日誌**: 在 `appsettings.json` 中設定：
   ```json
   {
     "Logging": {
       "LogLevel": {
         "Default": "Debug"
       }
     }
   }
   ```

2. **重建容器**: 
   ```bash
   docker-compose down
   docker-compose build --no-cache
   docker-compose up -d
   ```

3. **清除資料並重新開始**:
   ```bash
   docker-compose down -v
   docker-compose up -d
   ```

4. **聯絡支援**: 提供以下資訊
   - `docker-compose logs backend-api` 的完整輸出
   - `/health/worker` 端點的回應
   - Prometheus metrics 輸出
   - 資料庫查詢結果
