# 104 HR API 端點修正指南

## 目前狀態

| API | 目前端點 | 狀態 |
|-----|----------|------|
| 員工 | `/api/ed/emp` | ✓ 正常 (取得 5 筆資料) |
| 部門 | `/api/os/dept` | ✗ HTTP 440 |
| 部門層級 | `/api/os/dept_level` | ✗ HTTP 404 |

## 需要確認的資訊

請查看 `D:\Project\Sync104ToBpmErp\DOC\104HRMax_API欄位串接V2.17.0_20251229.xls` 的活頁簿 320 和 321，確認：

1. **部門 API 端點** - 正確的 URL 路徑
2. **部門層級 API 端點** - 正確的 URL 路徑
3. **請求參數** - 時間參數的名稱

## 可能的端點組合

根據員工 API (`/api/ed/emp`) 的模式，部門 API 可能是：

### 選項 1: 相同前綴
- 部門: `/api/ed/dept`
- 部門層級: `/api/ed/dept_level`

### 選項 2: 組織前綴
- 部門: `/api/org/dept`
- 部門層級: `/api/org/dept_level`

### 選項 3: 完整名稱
- 部門: `/api/ed/department`
- 部門層級: `/api/ed/department_level`

### 選項 4: 目前設定 (原始)
- 部門: `/api/os/dept`
- 部門層級: `/api/os/dept_level`

## 如何修正

找到正確端點後，修改 `appsettings.json`：

```json
{
  "HRApi": {
    "BaseUrl": "https://104demotest-api-server.hrmax.104.com.tw",
    "AuthEndpoint": "/api/auth/signIn",
    "EmployeeEndpoint": "/api/ed/emp",
    "DepartmentEndpoint": "【正確的部門端點】",
    "HierarchyEndpoint": "【正確的部門層級端點】",
    ...
  }
}
```

## 測試指令

```bash
cd D:\Project\Sync104ToBpmErp
dotnet run -- --test-api
```

## 注意事項

1. HTTP 440 可能是權限問題或參數錯誤
2. HTTP 404 表示端點不存在
3. 請確認 API 文件中的正確端點和參數名稱
