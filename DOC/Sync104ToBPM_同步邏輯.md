# 104 HR → BPM 同步邏輯

> 說明目前 `Sync104ToBpmErp` 專案「104 HR Max → BPM」這條線的實際同步方式。
> 程式碼：[Services/SyncService.cs](../Services/SyncService.cs)、[Services/BpmDatabaseService.cs](../Services/BpmDatabaseService.cs)

---

## 整體流程

```
GET /api/os/company → 取得 [CO_ID, CO_CODE] 清單（僅查詢比對，Organization 不寫入）

for each company（依 CO_ID 逐一處理）：
  1. GET /api/os/dept_level → BPM OrganizationUnitLevel（部門層級名稱，一律抓全部，不篩時間）
  2. GET /api/os/dept       → BPM OrganizationUnit（部門，依 E_SDATETIME/E_EDATETIME 維護日期區間）
  3. GET /api/ed/emp        → BPM Users → BPM Employee → BPM Functions（職稱/簽核，依 E_SDATETIME/E_EDATETIME 維護日期區間）
```

> **2026-09-07 修正**：`GET /api/os/dept` 跟 `GET /api/ed/emp` 都改用 `E_SDATETIME`/`E_EDATETIME`（維護日期）做時間區間查詢，涵蓋新增與異動兩種情況，跟 `appsettings.json` 的 `SyncSettings.BeginDays` 共用同一套區間邏輯：
> - **員工端**原本用 `C_SDATETIME`/`C_EDATETIME`（新增日期）篩選，這只抓得到新進員工，抓不到「既有員工被異動」(調部門、換主管、升職)。已改用 `E_SDATETIME`/`E_EDATETIME`（維護日期），對正式 104 API 實測確認可同時涵蓋兩種情況。
> - **部門端**原本欄位名稱就是對的（`E_SDATETIME`/`E_EDATETIME`），2026-09-03 一度誤判為篩選不可靠而改成每次抓全部；2026-09-07 直接對 104 API 實測釐清，篩選機制其實正常運作，先前持續抓到 0 筆單純是測試當時查詢區間不夠寬，涵蓋不到部門實際的維護日期，因此改回依區間查詢，不用每次都抓全部。
>
> 兩者共用 `BeginDays`：需要設多大，取決於「最久以前異動過、但一直沒被抓到」的資料距今多久——可對 104 API 直接查該筆記錄的 `E_DATETIME` 來精算需要多少天。

寫入順序固定：`Organization`（已存在）→ `OrganizationUnitLevel` → `OrganizationUnit` → `Users` → `Employee` → `Functions`，因為後面每一步都要用前面已寫入的 OID 當外鍵。

---

## OrganizationUnitLevel（部門層級名稱）— 2026-09-07 起不寫入

客戶反映這張表的資料不該被程式修改（屬於 BPM 管理端維護的基礎設定），且原本邏輯不管值有沒有變都會無條件 UPDATE、`objectVersion` 一直往上累加（實測有一筆被跑到 58）。已停用寫入，`SyncOrganizationUnitLevelsAsync` 改為直接回傳跳過結果，原本的 UPSERT 邏輯整段註解保留在程式碼裡（[BpmDatabaseService.cs](../Services/BpmDatabaseService.cs) 內 `/* ... */` 區塊），之後如需恢復可直接取消註解。

`OrganizationUnit.levelOID` 的對照查詢不受影響，仍直接讀這張表的既有資料。

---

## OrganizationUnit（部門）

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | 產生 32 碼亂數 |
| `id` | 104 `DEPT_CODE` |
| `organizationUnitName` | 104 `DEPT_NAME`，**去除開頭的部門代碼前綴**（104 常把代碼帶在名稱裡，如「A0450業務行政部」→ 只存「業務行政部」）|
| `managerOID` | 104 `LEADER_EMP_NO` → 查 `Users.OID` |
| `superUnitOID` | 104 `PARENT_DEPT_CODE` → 查 `OrganizationUnit.OID` |
| `levelOID` | 104 `DEPT_LEVEL_ID/NAME` → 查 `OrganizationUnitLevel.OID` |
| `organizationOID` | 104 `CO_ID` → 查 `Organization.OID` |
| `organizationUnitType` | 固定值 1 |
| `validType` | 104 `IS_ACT`（1=啟用／0=停用） |
| `objectVersion` | 新增1／更新+1 |

UPSERT 判斷鍵：`id`（DEPT_CODE）
寫入前先做拓樸排序，確保父部門一定比子部門先寫入。

---

## Users（登入帳號）

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | 產生 32 碼亂數 |
| `id` | 104 `EMP_NO` |
| `userName` | 104 `EMP_NAME` |
| `mailAddress` | 104 `OFFICE_EMAIL` |
| `phoneNumber` | 104 `OFFICE_TEL`（僅新增時寫入，更新時不覆蓋） |
| `leaveDate` | 104 `QUIT_DATE` |
| `ldapid` | 104 `EMP_EN_NAME` |
| `password` | 固定初始值 |
| `identificationType` | 固定值 `Employee` |
| `localeString` | 固定值 `zh_TW` |

UPSERT 判斷鍵：`id`（EMP_NO）
更新時只改 `userName`／`mailAddress`／`leaveDate`；`phoneNumber`／`password` 維持原值不覆蓋。

---

## Employee（員工歸屬）

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | 產生 32 碼亂數 |
| `employeeId` | 104 `EMP_NO` |
| `organizationOID` | 104 `CO_CODE` → 查 `Organization.OID`（**公司層級**，不是部門） |
| `userOID` | 查 `Users.OID` |
| `validTo` | 104 `QUIT_DATE` |
| `objectVersion` | 新增1／更新+1 |

UPSERT 判斷鍵：`employeeId`（EMP_NO）
更新時只改 `organizationOID`／`validTo`；`userOID` 維持原值不覆蓋。

---

## Functions（職稱／簽核歸屬）

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | 產生 32 碼亂數 |
| `occupantOID` | 104 `EMP_NO` → 查 `Users.OID` |
| `organizationUnitOID` | 104 `Dept1Code` → 查 `OrganizationUnit.OID` |
| `definitionOID` | 104 `JobName` → 查 `FunctionDefinition.functionDefinitionName`（限同公司；**只查詢不新增**，查無則跳過該員工）|
| `approvalLevelOID` | 先查 `FunctionLevel.functionLevelName = 職稱名稱`（同公司），職稱剛好對到主管職層級名稱（如「經理」「課長」）就代入該層級；對不到才 fallback 查 `functionLevelName = 'defaultLevel'`（**只查詢不新增**）|
| `specifiedManagerOID` | 查 `OrganizationUnit.managerOID`；**若該部門主管就是自己**，改沿 `superUnitOID` 往上層部門找「不是自己」的主管；**一路找到組織最頂層都找不到**（例如董事長，上面已無部門），維持自己是自己的直屬主管 |
| `isMain` | 固定值 1 |
| `objectVersion` | 新增1／更新+1 |

UPSERT 判斷鍵：`occupantOID` + `organizationUnitOID`
`FunctionDefinition`／`FunctionLevel` 由 BPM 管理端維護，本程式不寫入這兩張表。

> **2026-09-03 修正**：
> 1. `approvalLevelOID` 原本一律代入 `defaultLevel`，改成先比對職稱是否為主管職核決層級名稱，是則代入對應層級。
> 2. `specifiedManagerOID` 原本若員工自己是部門主管，會把自己填成自己的直屬主管；改成往上層部門找不是自己的主管。
>    ERP `gen_file.TA_GEN07`（`GetEmployeeManagerEmpNosAsync`）套用同一套邏輯。
>
> **2026-09-09 補修正**：往上層找主管的邏輯，如果一路找到組織最頂層（沒有 `superUnitOID` 可再往上）都找不到「不是自己」的主管，
> 改成維持原本自己是自己的直屬主管，不留空——因為 BPM 既有資料裡，組織最頂層（如董事長）本來就是以「自己是自己的主管」表示，
> 留空反而是跟既有慣例不一致的異常值。

---

## Functions（部門兼職／掛名主管，isMain=0）— 2026-09-09 新增

104 部門的 `LEADER_EMP_NO` 不一定等於該主管自己的 `DEPT1_CODE`（甚至可能跨公司，例如集團董事長被指定管理海外子公司的董事長室，但 104 從未把他登記成那家公司的員工）。這種情況下：

1. 幫他在**掛名部門**補一筆 `isMain=0` 的 `Functions` 記錄，職稱/核決層級沿用他自己「本職」(`isMain=1`) 的 Functions 記錄。
2. 若他在**該部門所屬公司底下完全沒有 `Employee` 記錄**（跨公司掛名的情況），額外補一筆 `Employee`（`organizationOID`=該公司），因為實測證實 BPM 展開部門時，會對部門底下每個 `Functions` 佔用者查 `Users+Employee WHERE Employee.organizationOID=該公司`，查無資料會導致 BPM 後端接口調用失敗、整個部門展開不了。客戶已確認正式環境允許「分公司掛不隸屬該公司的人當部門主管」，所以主動補資料而非等 104 修正。

實作：`SyncConcurrentDeptHeadFunctionsAsync`（[BpmDatabaseService.cs](../Services/BpmDatabaseService.cs)），排在 `SyncEmployeeFunctionsAsync` 之後執行。

---

## OID 產生

所有表 PK 皆為 `nchar(32)`，程式產生亂數後會查詢多張相關表確認不重複，才寫入。

## UPSERT 原則

所有表一律「先查是否存在 → 存在 UPDATE、不存在 INSERT」，沒有任何一步是刪除重建。
