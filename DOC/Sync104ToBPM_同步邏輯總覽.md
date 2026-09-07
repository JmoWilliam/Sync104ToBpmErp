# 104 HR → BPM 同步邏輯總覽

> 本文件整理 `Sync104ToBpmErp` 專案中「104 HR Max → BPM（電子簽核系統）」這條線目前的實際實作方式，
> 包含每個步驟寫入哪些 Table、欄位怎麼對應、OID 怎麼產生、UPSERT 判斷鍵是什麼，
> 以及開發過程中對正式 BPM 資料庫查證後做的關鍵修正。
>
> ERP（TIPTOP）那條線不在本文件範圍內，請參考 [api_erp_bpm_mapping.md](api_erp_bpm_mapping.md)。
> 職稱/簽核相關表的完整欄位規格請參考 [BPM/BPM_TableSchema.xlsx](BPM/BPM_TableSchema.xlsx)。
>
> 最後更新：2026-09-03

---

## 1. 整體流程

```
RunFullSyncAsync(startTime, endTime)
  │
  ├─ Step 1: GET /api/os/company → 取得 [CO_ID, CO_CODE] 清單
  │           （只查詢比對，不寫入 BPM Organization —— 公司資料已在 BPM 管理端建立）
  │
  └─ for each company (依 CO_ID 逐一 loop，單一公司失敗不影響其他公司)
        │
        ├─ Step 2: GET /api/os/dept_level → BPM OrganizationUnitLevel（部門層級名稱）
        │
        ├─ Step 3: GET /api/os/dept → BPM OrganizationUnit（部門）
        │                           → ERP gem_file / abd_file
        │
        └─ Step 4: GET /api/ed/emp → BPM Users → BPM Employee → BPM Functions（職稱/簽核）
                                    → ERP gen_file
```

**執行順序是硬性規定**，因為後面的表都靠外鍵串前面已經寫好的 OID：
`Organization`（已存在）→ `OrganizationUnitLevel` → `OrganizationUnit` → `Users` → `Employee` → `Functions`

實作位置：
- 排程協調：[Services/SyncService.cs](../Services/SyncService.cs)
- BPM 寫入邏輯：[Services/BpmDatabaseService.cs](../Services/BpmDatabaseService.cs)
- HR API 呼叫：[Services/HRApiService.cs](../Services/HRApiService.cs)

---

## 2. Step 1：公司清單（Organization）— 只查詢，不寫入

104 `/api/os/company` 回傳 `CO_ID` / `CO_CODE` / `CO_NAME`，程式**不會**寫入 BPM 的 `Organization` 表——這張表已經在 BPM 管理端建好了（含帳號/密碼/組織代號等，不是 104 能決定的資料）。

程式只做一件事：用 `CO_CODE` 查出 `Organization.OID`，後面所有表（`OrganizationUnit.organizationOID`、`FunctionDefinition.organizationOID`…）都要用這個 OID 當作「這是哪家公司」的依據。

```csharp
// BpmDatabaseService.cs — GetOrganizationOIDAsync()
SELECT [OID] FROM [Organization] WHERE [id] = @CoCode
```

---

## 3. Step 2：部門層級名稱 → `OrganizationUnitLevel`

104 `/api/os/dept_level`（例：董事長室／總經理室／處／部／課）

| BPM 欄位 | 型態 | 資料來源 |
|---|---|---|
| `OID` PK | nchar(32) | `GenerateOID()` |
| `objectVersion` | int | 新增1／更新+1 |
| `levelValue` | int | 104 `SORT_ORDER`（數字越小層級越高） |
| `organizationUnitLevelName` | nvarchar(100) | 104 `LEVEL_NAME` |
| `organizationOID` | nchar(32) FK→Organization | 104 `CO_ID` → 查 `Organization.OID` |
| `description` | ntext | 不填 |

**UPSERT 判斷鍵**：`levelValue` + `organizationOID`。

實作：`SyncOrganizationUnitLevelsAsync()`（[BpmDatabaseService.cs:317](../Services/BpmDatabaseService.cs)）

> ⚠️ 正式 BPM 的 `OrganizationUnitLevel` 表在畫面上還多一個 `rightType`（存取權限類型）欄位，但目前專案內 `BPM_DatabaseObject.sql` 匯出的 DDL 沒有這欄，程式目前**沒有寫這個欄位**。若之後發現需要，要另外確認。

---

## 4. Step 3：部門 → `OrganizationUnit`

104 `/api/os/dept`

| BPM 欄位 | 型態 | 資料來源 |
|---|---|---|
| `OID` PK | nchar(32) | `GenerateOID()` |
| `id` | nvarchar(100) | 104 `DEPT_CODE`（業務主鍵） |
| `organizationUnitName` | nvarchar(100) | 104 `DEPT_NAME` |
| `managerOID` | nchar(32) FK→**Users** | 104 `LEADER_EMP_NO` → 查 `Users.OID` |
| `superUnitOID` | nchar(32) FK→OrganizationUnit（自參照） | 104 `PARENT_DEPT_CODE` → 查 `OrganizationUnit.OID` |
| `objectVersion` | int | 新增1／更新+1 |
| `organizationUnitType` | int | 固定值 1（一般部門） |
| `levelOID` | nchar(32) FK→OrganizationUnitLevel | 104 `DEPT_LEVEL_ID/NAME` → 查 `OrganizationUnitLevel.OID` |
| `organizationOID` | nchar(32) FK→Organization | 104 `CO_ID` → 查 `Organization.OID` |
| `validType` | int | 104 `IS_ACT`（1=啟用 / 0=停用） |

**UPSERT 判斷鍵**：`id`（=`DEPT_CODE`）。

**寫入前會先做拓撲排序**（`TopologicalSortDepartments()`，[BpmDatabaseService.cs:63](../Services/BpmDatabaseService.cs)）：確保父部門一定比子部門先寫入，否則子部門查 `superUnitOID` 時找不到父部門的 OID。

實作：`SyncOrganizationUnitsAsync()`（[BpmDatabaseService.cs:148](../Services/BpmDatabaseService.cs)）

同時寫入 ERP `gem_file`（部門）與 `abd_file`（部門層級關係，`ABD01`=父、`ABD02`=子），這兩張表細節見 [api_erp_bpm_mapping.md](api_erp_bpm_mapping.md)。

---

## 5. Step 4：員工 → `Users` + `Employee`

104 `/api/ed/emp`。BPM 用兩張表表示一個員工：`Users`（登入帳號）先寫，`Employee`（員工與組織的歸屬關係）後寫，因為 `Employee.userOID` 要等 `Users.OID` 先產生。

### 5.1 `Users`

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | `GenerateOID()` |
| `id` | 104 `EMP_NO`（業務主鍵） |
| `userName` | 104 `EMP_NAME` |
| `password` | 固定初始值（appsettings 可設定，預設 `0000`） |
| `mailAddress` | 104 `OFFICE_EMAIL` |
| `phoneNumber` | 104 `OFFICE_TEL`（僅新增時寫入，更新時不覆蓋，見下方說明） |
| `leaveDate` | 104 `QUIT_DATE` |
| `ldapid` | 104 `EMP_EN_NAME`（英文姓名） |
| `identificationType` | 固定值 `Employee` |
| `localeString` | 固定值 `zh_TW` |
| 其餘欄位 | 固定預設值（`enableSubstitute=0`、`userTaskDisplay=1`… 詳見程式碼） |

**UPSERT 判斷鍵**：`id`（=`EMP_NO`）。

> 📌 **2026-07-16 依客戶回覆**：既有帳號**更新時只改** `userName`、`mailAddress`、`leaveDate` 三個欄位；`phoneNumber`、`password` 客戶確認不需要覆蓋既有值，維持原值不動。這是刻意的設計，不是漏寫。

### 5.2 `Employee`

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | `GenerateOID()` |
| `employeeId` | 104 `EMP_NO` |
| `organizationOID` | ⚠️ 見下方「重要修正」— 104 `CO_CODE` → 查 `Organization.OID`（**公司層級**） |
| `userOID` | 查 `Users.OID`（本批次剛寫好的） |
| `objectVersion` | 新增1／更新+1 |
| `validTo` | 104 `QUIT_DATE` |

**UPSERT 判斷鍵**：`employeeId`（=`EMP_NO`）。更新時只改 `organizationOID`、`validTo`；`userOID` 客戶備註是系統關聯鍵，不應變動，維持原值。

實作：`SyncEmployeesAsync()`（[BpmDatabaseService.cs:435](../Services/BpmDatabaseService.cs)）

#### ⚠️ 重要修正：`Employee.organizationOID` 原本寫錯，已改正

最初（依 BPM 官方 Table 說明文件字面推測）以為 `Employee.organizationOID` 應該指向**部門**（`OrganizationUnit.OID`），第一次全量同步後對正式 BPM DB 做全庫健檢，發現：

- 全庫 1705 筆既有 `Employee`，**1689 筆（99%）的 `organizationOID` 其實指向 `Organization`（公司），不是 `OrganizationUnit`（部門）**
- 唯一「指向部門」的 16 筆，剛好就是我們自己第一次同步寫入的那批（也就是我們寫錯了）

結論：`Employee.organizationOID` 在這套系統裡語意上是「員工屬於哪家公司」，**部門/職稱/簽核歸屬要靠 `Functions` 表**（見第 6 節），不是靠 `Employee.organizationOID`。已修正為：

```csharp
string? organizationOID = await GetOrganizationOIDAsync(connection, transaction, emp.CompanyCode);
```

連帶影響：原本 `GetEmployeeManagerEmpNosAsync()`（供 ERP `gen_file.TA_GEN07` 直屬主管工號使用）是靠 `Employee.organizationOID → OrganizationUnit` 這條路查主管，`organizationOID` 改語意後這條路會斷，因此也一併改成直接用 104 `Dept1Code` 查 `OrganizationUnit.managerOID`，不再繞經 `Employee.organizationOID`（[BpmDatabaseService.cs:659](../Services/BpmDatabaseService.cs)）。

---

## 6. Step 4.1：職稱／簽核歸屬 → `Functions`（2026-09-03 新增）

這是本次補齊「職稱、簽核邏輯」缺口的核心。`Functions`（組織單元職務）把「員工＋部門＋職稱＋核決層級＋指定主管」串在一起，是 BPM 簽核流程真正依賴的資料，必須排在 `Users` + `Employee` + `OrganizationUnit` 都同步完成後才執行。

| BPM 欄位 | 資料來源 | 查證備註 |
|---|---|---|
| `OID` PK | `GenerateOID()` | |
| `objectVersion` | 新增1／更新+1 | |
| `occupantOID` | 104 `EMP_NO` → 查 `Users.OID` | ⚠️ 對正式 DB 全庫 1827 筆 `Functions` 查證，**100% 對應 `Users.OID`**，不是 `Employee.OID`（跟原始文件字面描述相反）|
| `organizationUnitOID` | 104 `Dept1Code` → 查 `OrganizationUnit.OID` | |
| `definitionOID` | 104 `JobName`（職稱）→ 查 `FunctionDefinition.functionDefinitionName`（限定同一公司） | **只查詢比對，不自動新增**。查無對應職稱時記警告並跳過該員工，不中斷其他人（見下方原因） |
| `approvalLevelOID` | 查 `FunctionLevel.functionLevelName = 'defaultLevel'`（限定同一公司） | 全庫 1827 筆 `Functions` 中有 1401 筆（77%）核決層級就是這個值；其餘是主管職專屬層級（總經理／副總經理／協理／經理…），細分規則待客戶確認職等對照表後再處理 |
| `specifiedManagerOID` | 沿用 `OrganizationUnit.managerOID` | ⚠️ 查證全庫 1763 筆非空值，**100% 對應 `Users.OID`**，跟 `occupantOID` 一樣不是 `Employee.OID` |
| `isMain` | 固定值 `1` | 104 目前只回傳單一部門（`Dept1Code`），先固定視為主要部門 |

**UPSERT 判斷鍵**：`occupantOID` + `organizationUnitOID`（代表「這個人在這個部門」）。同一人如果在 BPM 裡本來就有其他部門的兼職 `Functions` 記錄，因為 key 對不上不會被異動到。

**為什麼 `FunctionDefinition` / `FunctionLevel` 不自動新增**：查證時發現正式 BPM 這兩張表已經有資料（`FunctionDefinition` 517 筆、`FunctionLevel` 每家公司固定 10 筆含 `defaultLevel`），屬於低異動頻率、由 BPM 管理端維護的基礎設定。自動新增職稱或核決層級需要業務判斷（命名規則、要不要併入現有職稱、核決層級要對應到哪一階），不是單純的資料搬移，所以設計成「只查、不寫」，查不到就跳過並記警告，讓 BPM 管理員自己決定要不要建檔，而不是讓程式自己亂造資料。

實作：`SyncEmployeeFunctionsAsync()`（[BpmDatabaseService.cs:706](../Services/BpmDatabaseService.cs)）

---

## 7. OID 產生機制

BPM 所有表的 PK 都是 `nchar(32)`，由程式產生亂數英數字並確認不重複：

```csharp
// OidHelper.GenerateUniqueAsync — 產生32碼英數字亂碼，並呼叫傳入的檢查函式確認不重複
```

每次產生後，會查詢**所有相關表**確認沒有撞號，不是只查自己要寫入的那張表：

| 寫入哪張表時 | 會一併檢查哪些表的 OID |
|---|---|
| `OrganizationUnit` | 自己 |
| `OrganizationUnitLevel` | 自己 |
| `Users` | Users, Employee, OrganizationUnit, Organization, OrganizationUnitLevel |
| `Employee` | Employee, Users, OrganizationUnit, Organization, OrganizationUnitLevel |
| `Functions` | Functions, Users, Employee, OrganizationUnit, Organization, OrganizationUnitLevel, FunctionDefinition, FunctionLevel |

---

## 8. UPSERT 總表

所有寫入都是「先查是否存在 → 存在就 UPDATE，不存在才 INSERT」，沒有任何一張表是先刪除再新增：

| BPM Table | UPSERT 判斷鍵 | 更新時會改哪些欄位 |
|---|---|---|
| `OrganizationUnitLevel` | `levelValue` + `organizationOID` | 名稱、objectVersion |
| `OrganizationUnit` | `id`（DEPT_CODE） | 名稱、主管、上層部門、層級、公司、有效狀態、objectVersion |
| `Users` | `id`（EMP_NO） | `userName`、`mailAddress`、`leaveDate`、objectVersion（`phoneNumber`/`password` 不覆蓋）|
| `Employee` | `employeeId`（EMP_NO） | `organizationOID`、`validTo`、objectVersion（`userOID` 不覆蓋）|
| `Functions` | `occupantOID` + `organizationUnitOID` | `definitionOID`、`approvalLevelOID`、`specifiedManagerOID`、`isMain`、objectVersion |

**為什麼不能刪除重建**：OID 是隨機產生的，刪除重建會讓所有 OID 換掉，BPM 裡任何參照到舊 OID 的資料（簽核紀錄、待辦事項等）都會斷鏈，是不可逆的破壞。

---

## 9. 目前已知限制／待確認事項

| 項目 | 現況 | 待確認 |
|---|---|---|
| `OrganizationUnitLevel.rightType` | 程式沒寫這個欄位 | 正式 DB 有此欄，本地 DDL 匯出版本沒有，需與 BPM 端確認是否要補 |
| `Functions.approvalLevelOID` 細分規則 | 一律用 `defaultLevel` | 管理職是否要依職等對應到專屬層級，需客戶確認職等對照表 |
| `FunctionDefinition` 找不到職稱時 | 記警告、跳過該員工的 Functions | 要維持人工在 BPM 後台建檔，還是改成程式自動新增 |
| `OrganizationUnitType` | 固定值 1 | 是否要依 104 `ORG_TYPE_CODE` 細分 |
| `gen_file.TA_GEN08`（銀行帳號） | 無資料來源 | 待 104 端確認欄位對應 |
| `OrganizationUnitProperty` / `OrgUnit_OrgUnitProperty`（組織屬性標籤）| 未同步 | 與職稱/簽核無直接關聯，是否要納入同步範圍待確認 |

---

## 10. 參考文件

- 各 BPM Table 完整欄位規格（含哪些已同步／哪些待處理）：[BPM/BPM_TableSchema.xlsx](BPM/BPM_TableSchema.xlsx)
- 104 API ↔ ERP/BPM 欄位對照總表：[api_erp_bpm_mapping.md](api_erp_bpm_mapping.md)
- 最初的設計草稿（部分內容已被本文件與上述查證結果取代）：[程式邏輯.md](程式邏輯.md)
