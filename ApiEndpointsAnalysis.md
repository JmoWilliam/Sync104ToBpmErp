# 104 HR API 端點分析

## 目前設定

```json
{
  "BaseUrl": "https://104demotest-api-server.hrmax.104.com.tw",
  "AuthEndpoint": "/api/auth/signIn",        // ✓ 正常 (取得 Token)
  "EmployeeEndpoint": "/api/ed/emp",          // ✓ 正常 (取得 5 筆員工)
  "DepartmentEndpoint": "/api/os/dept",       // ✗ HTTP 440
  "HierarchyEndpoint": "/api/os/dept_level"   // ✗ HTTP 404
}
```

## 測試結果

| 端點 | 狀態 | 回應 |
|------|------|------|
| `/api/auth/signIn` | ✓ 200 | Token 取得正常 |
| `/api/ed/emp` | ✓ 200 | 取得 5 筆員工資料 |
| `/api/os/dept` | ✗ 440 | 可能是權限或參數問題 |
| `/api/os/dept_level` | ✗ 404 | 端點不存在 |

## 可能的問題

### 1. DepartmentEndpoint (/api/os/dept) - HTTP 440

HTTP 440 是 104 API 的自定義錯誤，可能原因：
- **權限不足**：帳號沒有部門資料查詢權限
- **參數錯誤**：時間參數名稱可能不正確
- **端點版本**：可能是舊版端點，新版已更改

**建議嘗試的端點：**
- `/api/ed/dept` (與員工端點一致的前綴)
- `/api/org/dept`
- `/api/v2/os/dept`

### 2. HierarchyEndpoint (/api/os/dept_level) - HTTP 404

HTTP 404 表示端點不存在，可能：
- 端點名稱錯誤
- 該功能在 Demo 環境未開放
- 已合併到其他端點

**建議嘗試的端點：**
- `/api/ed/dept_level`
- `/api/org/dept_level`
- `/api/os/dept/level`
- `/api/os/dept/hierarchy`

## 建議測試方式

### 方法 1：逐一測試可能的端點

修改 `appsettings.json` 中的端點，逐一測試：

```json
// 測試 1: 部門端點
"DepartmentEndpoint": "/api/ed/dept"

// 測試 2: 部門層級端點  
"HierarchyEndpoint": "/api/ed/dept_level"
```

### 方法 2：查看 104 API 文件

確認以下資訊：
1. 正確的部門查詢端點
2. 正確的部門層級端點
3. 所需的請求參數名稱

### 方法 3：聯繫 104 技術支援

提供以下資訊給 104：
- 帳號：`geshangapi`
- Company ID：`29`
- 問題：部門和部門層級端點回傳 440/404

## 暫時的替代方案

如果部門 API 暫時無法使用，可以：

1. **從員工資料提取部門資訊**
   - 員工資料中有 `DEPT_CODE` 和 `DEPT_NAME`
   - 可以彙整出部門清單

2. **手動維護部門對照表**
   - 建立本機的部門代碼對照表
   - 定期手動更新

## 下一步行動

1. 確認 104 API 文件中的正確端點
2. 或嘗試修改端點進行測試
3. 或聯繫 104 技術支援確認權限
