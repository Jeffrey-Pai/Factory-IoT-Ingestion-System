# 重新打包與發布流程（Release Guide）

改完程式碼之後，怎麼把它變成正在跑的服務。

這份文件回答的是「我改了 X，要重跑哪些指令？」。系統怎麼跑起來、怎麼監控、怎麼查問題請看 [OPERATIONS.md](./OPERATIONS.md)；本機開發環境設定請看 [DEVELOPMENT.md](./DEVELOPMENT.md)。

---

## 目錄

1. [先搞清楚一件事：什麼東西是打包進 image 的](#1-先搞清楚一件事什麼東西是打包進-image-的)
2. [決策表：改了什麼 → 跑什麼](#2-決策表改了什麼--跑什麼)
3. [標準流程](#3-標準流程)
4. [分情境操作](#4-分情境操作)
5. [發布後驗證](#5-發布後驗證)
6. [回滾](#6-回滾)
7. [常見坑](#7-常見坑)
8. [附錄：一鍵重發腳本](#8-附錄一鍵重發腳本)

---

## 1. 先搞清楚一件事：什麼東西是打包進 image 的

會踩到的坑幾乎都源自這張表。**左邊兩欄是編譯進 image 的 —— 改了就一定要重新 build**；右邊是啟動時才讀的，重啟就夠。

| 改動類型 | 是否需要 `build` | 為什麼 |
|----------|:---:|--------|
| C# 程式碼（`src/**/*.cs`） | ✅ 要 | `dotnet publish` 在 build stage 編成 DLL |
| 前端程式碼（`frontend/src/**`） | ✅ 要 | Vite 在 build stage 打包成 `dist/`，**不是**執行時才編譯 |
| `frontend/nginx.conf` | ✅ 要 | 被 `COPY` 進 nginx image |
| `appsettings.json` | ✅ 要 | 跟著 `dotnet publish` 一起輸出 |
| `docker-compose.yml` 的環境變數 | ❌ 不用 | 容器啟動時注入 |
| `prometheus.yml`、`rabbitmq.conf` | ❌ 不用 | 用 volume 掛進去的 |
| EF Core Migration 檔 | ✅ 要（跟 C# 一起） | 但**不需要手動執行** —— 見 [情境 E](#情境-e改到-entity--dbcontextmigration) |

> ⚠️ **最常見的誤會**：前端改了 `.tsx` 卻只跑 `docker compose restart frontend`。nginx 只是把 build stage 產生的靜態檔案吐出去，重啟它不會重新編譯任何東西 —— 你會看到一模一樣的舊畫面。

---

## 2. 決策表：改了什麼 → 跑什麼

| 你改了 | 指令 | 停機時間 |
|--------|------|----------|
| 只有後端 C#（Repository、Worker、API 端點…） | `docker compose up -d --build backend-api` | 數秒 |
| 只有前端（`frontend/src/**`、`nginx.conf`） | `docker compose up -d --build frontend` | 幾乎沒有 |
| 前後端都改（**本次修改就是這種**） | `docker compose up -d --build backend-api frontend` | 數秒 |
| 只有模擬器 | `docker compose up -d --build simulator` | 數秒 |
| 只有 `docker-compose.yml` 的環境變數 | `docker compose up -d backend-api` | 數秒 |
| Entity／DbContext（含新 migration） | 先 `dotnet ef migrations add`，再同「只有後端」 | 數秒 |
| 不確定 / 想全部重來 | `docker compose up -d --build` | 數秒 |

`up -d --build` 會 build 完才換掉舊容器，所以「數秒」指的是容器重啟本身，不含 build 時間。**build 期間舊版本照常服務**。

---

## 3. 標準流程

```bash
# 0. 拿到最新程式碼
git pull

# 1. 本機先驗證，不要把編不過的東西送進 Docker（build 一次要幾分鐘）
dotnet build                    # 後端編得過嗎
dotnet test                     # 測試過嗎
cd frontend && npm run build     # 前端型別檢查 + 打包過嗎
cd ..

# 2. 重新打包並換掉容器（只列你改到的服務）
docker compose up -d --build backend-api frontend

# 3. 確認新容器起來了
docker compose ps
docker compose logs -f --tail=50 backend-api
```

看到這幾行就代表後端起好了：

```
Running database migrations...
Database migrations completed successfully
Data lifecycle worker starting. Interval: 30s, lag: 120s. ...
Successfully connected to RabbitMQ at rabbitmq
```

然後跳到[第 5 節驗證](#5-發布後驗證)。

---

## 4. 分情境操作

### 情境 A：只改後端 C#

```bash
docker compose up -d --build backend-api
```

`simulator` 跟 `backend-api` 共用 `Domain`／`Application` 兩個專案。**如果你改到這兩層**（例如 `Telemetry` entity），模擬器也要一起重發：

```bash
docker compose up -d --build backend-api simulator
```

只改 `Infrastructure`／`Presentation` 則不用 —— 模擬器沒相依它們。

### 情境 B：只改前端

```bash
docker compose up -d --build frontend
```

改完之後**在瀏覽器按 Ctrl+F5（或 Cmd+Shift+R）強制重新整理**。原因見[第 7 節](#7-常見坑)。

### 情境 C：只改環境變數（不動程式碼）

例如把 `DataRetention__RosterTailMinutes` 從 15 改成 30：

```bash
# 編輯 docker-compose.yml 後
docker compose up -d backend-api      # 注意：沒有 --build
```

Compose 會偵測到設定變了，用**同一個 image** 重建容器。這是最便宜的一種發布。

### 情境 D：改前端顯示邏輯而已，想快速看效果

開發階段不要用 Docker，用 Vite dev server（有 hot reload，改了立刻看到）：

```bash
docker compose up -d backend-api rabbitmq mssql simulator   # 後端跑在 Docker
cd frontend && npm run dev                                   # 前端跑在本機 :5173
```

`vite.config.ts` 已經把 `/api` 與 `/health` 代理到 `localhost:8080`，不需要額外設定。滿意了再走情境 B 打包。

### 情境 E：改到 Entity / DbContext（migration）

這是唯一需要多一個步驟的情況。

```bash
# 1. 產生 migration（在本機，需要 dotnet-ef 工具）
dotnet ef migrations add <描述性名稱> \
  --project src/FactoryIoT.Infrastructure \
  --startup-project src/FactoryIoT.Presentation

# 2. 一定要打開產生的檔案看過再送出
#    src/FactoryIoT.Infrastructure/Migrations/<timestamp>_<名稱>.cs

# 3. 照情境 A 重發
docker compose up -d --build backend-api
```

**不需要手動跑 `dotnet ef database update`。** `Program.cs` 在啟動時會呼叫 `Database.MigrateAsync()`，容器一起來就自己套用了。日誌會出現 `Running database migrations...`。

> ⚠️ Migration 失敗時容器會直接掛掉（`Program.cs` 是 `throw` 出去的），這是刻意的 —— 寧可不啟動，也不要用一個 schema 對不上的 API 服務流量。這時看 `docker compose logs backend-api` 找 SQL 錯誤。

### 情境 F：全部重來（image 或狀態怪怪的）

```bash
docker compose build --no-cache          # 完全不用快取重 build
docker compose up -d --force-recreate
```

**這不會刪資料** —— 資料在 named volume 裡，跟容器生命週期無關。真的要清空資料庫是 `docker compose down -v`（`-v` 會刪掉 volume，資料救不回來）。

---

## 5. 發布後驗證

### 5.1 基本健康檢查

```bash
curl -s localhost:8080/health          # {"status":"healthy"}
curl -s localhost:8080/health/worker   # isHealthy: true
docker compose ps                      # 每個服務都要是 Up / healthy
```

### 5.2 針對本次改動：機台燈號與最後回報時間

這次改的是「名冊查詢疊加尚未聚合的原始列」，所以要驗的是 **`lastSeen` 有沒有跟著現在走**：

```bash
# 看第一台機台的 lastSeen，跟現在時間比對
curl -s localhost:8080/api/v1/machines | head -c 400
date -u +"%Y-%m-%dT%H:%M:%SZ"
```

**通過標準：`lastSeen` 與 UTC 現在時間差在 10 秒以內。**
若差 2～3 分鐘，代表疊加沒有生效（見下方排查）。

連續看兩次，確認它真的在動：

```bash
for i in 1 2 3; do
  curl -s localhost:8080/api/v1/machines | grep -o '"lastSeen":"[^"]*"' | head -1
  sleep 5
done
```

三行的秒數應該遞增。

接著開 <http://localhost:8081> 看 UI：

- 機台總覽表格的燈號應該是**綠色且有呼吸動畫**
- 「最後回報」欄應該顯示「剛剛」或個位數秒數，而且**每秒自己往上跳**（不需要等下一次輪詢）
- 把右上角更新頻率切到「30 秒」，燈號**不應該**在下次更新前變灰

### 5.3 排查：`lastSeen` 還是落後好幾分鐘

按順序查：

| 檢查 | 指令 | 代表什麼 |
|------|------|----------|
| 新容器真的有換上去嗎 | `docker inspect -f '{{.Created}}' backend-api` | 應該是你剛剛發布的時間 |
| 資料真的有進來嗎 | `curl -s localhost:8080/health/worker` | `timeSinceLastMessage` 應該是秒級 |
| 聚合工作正常嗎 | `curl -s localhost:8080/api/v1/data-lifecycle` | 看 `tiers[].lagSeconds`，正常約 120～180 秒 |
| 疊加視窗被夾住了嗎 | 同上，`lagSeconds` 若 > `RosterTailMinutes × 60` | 聚合停擺了，先修聚合 |

`data-lifecycle` 端點是這次改動的關鍵儀表：`lagSeconds` 是名冊本身的落後量，**它本來就會是 120～180 秒，這是正常的**；疊加層負責把這段補上。

---

## 6. 回滾

服務都是無狀態的，回滾就是把舊 image 換回來。

```bash
# 方法一：回到上一個 commit 重 build（最直覺）
git log --oneline -5
git checkout <上一個好的 commit>
docker compose up -d --build backend-api frontend

# 方法二：發布前先打標籤，回滾時直接指回去
docker tag factory-iot-ingestion-system-backend-api:latest backend-api:known-good
```

> ⚠️ **有 migration 的版本不能這樣直接回滾。** 舊版程式碼配上新版 schema 不一定跑得起來，而且 EF 的 `Down()` 若涉及刪欄位就是實質資料遺失。要回滾含 migration 的發布，先確認 `Down()` 做了什麼，必要時從備份還原。本次改動**沒有 migration**，可以放心直接回滾。

---

## 7. 常見坑

**① 前端改了但畫面沒變**
先確認你跑的是 `--build` 而不是 `restart`。若確定 build 過了，就是瀏覽器快取 `index.html`（`/assets/` 下的檔案有 hash 指紋、快取一年沒問題，但 `index.html` 本身沒有 no-cache 標頭）。按 Ctrl+F5 強制重新整理即可。

**② `docker compose build` 沒有反映我的修改**
Docker 的 layer 快取是照檔案內容算的，正常情況會正確失效。若真的卡住，用 `docker compose build --no-cache backend-api`。

**③ 後端起不來，日誌停在 migration**
資料庫還沒 ready，或 migration 本身有錯。`docker compose logs mssql` 看資料庫狀態；compose 已經設了 `depends_on: service_healthy`，通常只是慢，等一下會自己重試。

**④ 前端出現「無法連線到後端 API」**
nginx 代理不到 `backend-api:8080`。多半是後端還在 migration，或後端容器掛了 —— `docker compose ps` 一看就知道。nginx 用的是請求時解析 DNS，所以後端恢復後**不用重啟 frontend**，會自己接回去。

**⑤ 改了 `docker-compose.yml` 卻沒生效**
用了 `restart` 而不是 `up -d`。`restart` 只是重跑同一個容器，不會套用新的環境變數；`up -d` 才會偵測設定變更並重建容器。

**⑥ 磁碟被 image 吃滿**
每次 build 都留下舊的 dangling image：`docker image prune -f`（安全，只刪沒有 tag 也沒被使用的）。

---

## 8. 附錄：一鍵重發腳本

放在專案根目錄存成 `redeploy.sh`：

```bash
#!/usr/bin/env bash
# 用法：./redeploy.sh [服務名...]   例：./redeploy.sh backend-api frontend
#      不給參數就全部重發
set -euo pipefail

echo "▶ 本機驗證…"
dotnet build  --nologo
dotnet test   --nologo --verbosity quiet
( cd frontend && npm run build )

echo "▶ 重新打包並替換容器…"
# 不給參數時不能展開空陣列，所以分開寫
if [ $# -eq 0 ]; then
  docker compose up -d --build
else
  docker compose up -d --build "$@"
fi

echo "▶ 等待後端就緒…"
for i in $(seq 1 30); do
  if curl -sf localhost:8080/health > /dev/null; then
    echo "✓ 後端已就緒"
    break
  fi
  sleep 2
done

docker compose ps
```

```bash
chmod +x redeploy.sh
./redeploy.sh backend-api frontend
```

先驗證再打包是刻意的順序 —— 編譯錯誤在本機幾秒就知道，在 Docker 裡要等好幾分鐘才失敗。
