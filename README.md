# Sync104ToBpmErp - HR 資料同步排程程式

## 專案簡介

此程式用於將 HR 系統的部門/員工/部門層級資料同步到電子簽核系統 (BPM) 和 ERP 系統。

## 功能特色

- 從 HR API 取得員工、部門、部門層級資料
- **支援時間範圍查詢** - 可指定開始時間和截止時間
- 使用 API Key 取得 Access Token 後進行認證呼叫
- 同步資料到 BPM (MS-SQL 2008) 和 ERP (Oracle 11g)
- 使用 Dapper 進行資料庫操作
- **完整的文字檔 Log 記錄**:
  - `SyncLog_YYYYMMDD.txt` - 一般執行記錄
  - `ErrorLog_YYYYMMDD.txt` - 錯誤記錄 (包含同步失敗明細)
- **詳細的同步資訊**: 資料庫連線狀態、抓取筆數、同步進度、失敗明細
- 參數化設定 (appsettings.json)

## 技術架構

- **.NET 8.0** - 開發框架
- **Dapper** - ORM 資料存取
- **Microsoft.Data.SqlClient** - MS-SQL 連線
- **Oracle.ManagedDataAccess.Core** - Oracle 連線
- **Serilog** - 日誌記錄
- **HttpClient** - HR API 呼叫

## 專案結構

```
Sync104ToBpmErp/
├── appsettings.json          # 設定檔 (API URL, 資料庫連線等)
├── Program.cs                # 程式入口點 (支援命令列參數和互動模式)
├── Models/                   # 資料模型
├── Configuration/            # 設定類別
├── Services/                 # 服務層
│   ├── LoggerService.cs      # 日誌服務 (SyncLog + ErrorLog)
│   ├── HRApiService.cs       # HR API 服務 (支援時間範圍查詢)
│   ├── BpmDatabaseService.cs # BPM 資料庫服務 (MS-SQL)
│   ├── ErpDatabaseService.cs # ERP 資料庫服務 (Oracle)
│   └── SyncService.cs        # 同步協調服務
└── SQL/                      # 資料庫結構
```

## 使用方式

### 編譯專案
```bash
cd D:\Project\Sync104ToBpmErp
dotnet build
dotnet publish -c Release -o .\publish
```

### 執行方式

#### 方式 1: 互動模式 (預設)
```bash
Sync104ToBpmErp.exe
```
程式會提示輸入開始時間和截止時間。

#### 方式 2: 命令列參數
```bash
# 執行同步 (帶時間參數)
Sync104ToBpmErp.exe --sync "2024-01-01 00:00:00" "2024-12-31 23:59:59"

# 簡寫
Sync104ToBpmErp.exe -s "2024-01-01" "2024-12-31"
```

#### 方式 3: 測試連線
```bash
Sync104ToBpmErp.exe --test-connection
# 或
Sync104ToBpmErp.exe -t
```

#### 方式 4: 顯示說明
```bash
Sync104ToBpmErp.exe --help
```

### 時間格式

支援以下格式:
- `yyyy-MM-dd HH:mm:ss` (例如: `2024-01-15 09:30:00`)
- `yyyy-MM-dd` (例如: `2024-01-15`，會自動轉為 `2024-01-15 00:00:00`)

## Log 檔案說明

所有 Log 檔案預設存放在 `Logs/` 目錄下:

### 1. SyncLog_YYYYMMDD.txt - 一般執行記錄
記錄內容包含:
- 程式啟動/結束時間
- 資料庫連線測試結果
- HR API 呼叫資訊 (取得多少筆資料)
- 同步進度 (每 100 筆記錄)
- 同步完成統計

範例:
```
[2024-01-15 09:30:00] [INF] [程式啟動] HR 資料同步程式啟動
[2024-01-15 09:30:01] [INF] [連線測試] BPM (MS-SQL) - 連線成功
[2024-01-15 09:30:02] [INF] [連線測試] ERP (Oracle) - 連線成功
[2024-01-15 09:30:05] [INF] [HR API] 成功取得 150 筆員工資料
[2024-01-15 09:30:10] [INF] [BPM (MS-SQL)] 員工資料同步進度: 100/150
[2024-01-15 09:30:15] [INF] [同步完成] 員工資料同步完成 - BPM: 150/150, ERP: 150/150
```

### 2. ErrorLog_YYYYMMDD.txt - 錯誤記錄
記錄內容包含:
- 資料庫連線失敗詳細錯誤
- 同步失敗的資料明細 (哪筆資料、什麼錯誤)
- 交易回滾事件

範例:
```
[2024-01-15 09:30:20] [ERR] [同步失敗] Employee | SYNC | Key: E001 | 錯誤: 資料行太大
[2024-01-15 09:30:21] [ERR] [同步失敗] Department | UPDATE | Key: D001 | 錯誤: 違反唯一條件約束
```

## 設定檔說明

編輯 `appsettings.json` 設定以下項目：

### HR API 設定
```json
"HRApi": {
    "BaseUrl": "https://hr-api.example.com",
    "AuthEndpoint": "/api/auth/token",
    "EmployeeEndpoint": "/api/employees",
    "DepartmentEndpoint": "/api/departments",
    "HierarchyEndpoint": "/api/hierarchy",
    "ApiKey": "YOUR_API_KEY_HERE"
}
```

### 資料庫連線設定
```json
"BpmDatabase": {
    "ConnectionString": "Server=BPMSERVER;Database=BPMDB;User Id=bpmuser;Password=bpmpassword;",
    "Provider": "MSSQL"
},
"ErpDatabase": {
    "ConnectionString": "Data Source=ERPSERVER:1521/ORCL;User Id=erpuser;Password=erppassword;",
    "Provider": "Oracle"
}
```

### 同步設定
```json
"SyncSettings": {
    "BatchSize": 100,
    "LogDirectory": "Logs",
    "SyncIntervalMinutes": 60
}
```

## Windows 工作排程器設定

1. 開啟「工作排程器」
2. 建立基本工作
3. 設定觸發條件 (例如每小時執行)
4. 動作選擇「啟動程式」
5. 程式路徑: `D:\Project\Sync104ToBpmErp\publish\Sync104ToBpmErp.exe`
6. **加入引數** (時間範圍): `--sync "2024-01-01 00:00:00" "2024-12-31 23:59:59"`
7. 完成設定

## 資料表結構

執行 `SQL/BPM_Schema.sql` 和 `SQL/ERP_Schema.sql` 建立資料表。

### BPM (MS-SQL)
- `BPM_EMPLOYEE` - 員工資料
- `BPM_DEPARTMENT` - 部門資料
- `BPM_DEPT_HIERARCHY` - 部門層級關係

### ERP (Oracle)
- `ERP_EMPLOYEE` - 員工資料
- `ERP_DEPARTMENT` - 部門資料
- `ERP_DEPT_HIERARCHY` - 部門層級關係

## 注意事項

1. **資安環境**: 客戶環境無法遠端連線，請將編譯好的程式和設定檔部署到客戶端執行
2. **Log 除錯**: 執行後檢查 Log 檔案確認執行狀況
   - `SyncLog_YYYYMMDD.txt` - 查看執行流程
   - `ErrorLog_YYYYMMDD.txt` - 查看失敗明細
3. **連線測試**: 首次部署建議先執行 `--test-connection` 測試連線
4. **資料備份**: 建議同步前先備份目標資料庫
5. **時間範圍**: 排程執行時請確認時間範圍參數正確

## 錯誤處理

- 所有錯誤都會記錄到 Error Log，包含失敗的資料 Key 和錯誤訊息
- 單筆資料失敗不會影響其他資料，會繼續處理下一筆
- 交易機制確保資料一致性 (發生嚴重錯誤時會回滾)
- 程式結束時會回傳 Exit Code (0=成功, 1=失敗)
