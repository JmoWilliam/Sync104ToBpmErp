# 104 HR → BPM 同步邏輯

> 說明目前 `Sync104ToBpmErp` 專案「104 HR Max → BPM」這條線的實際同步方式。
> 程式碼：[Services/SyncService.cs](../Services/SyncService.cs)、[Services/BpmDatabaseService.cs](../Services/BpmDatabaseService.cs)

---

## 整體流程

```
GET /api/os/company → 取得 [CO_ID, CO_CODE] 清單（僅查詢比對，Organization 不寫入）

for each company（依 CO_ID 逐一處理）：
  1. GET /api/os/dept_level → BPM OrganizationUnitLevel（部門層級名稱）
  2. GET /api/os/dept       → BPM OrganizationUnit（部門）※ 2026-09-03 起改為每次抓全部，不做時間區間篩選
  3. GET /api/ed/emp        → BPM Users → BPM Employee → BPM Functions（職稱/簽核）
```

> **2026-09-03 修正**：`GET /api/os/dept` 原本用 `E_SDATETIME`/`E_EDATETIME` 做異動時間區間查詢，
> 但部門的異動時間戳記不一定會反映每種欄位變更（例如主管改派），導致部門已在 HR 端更新卻抓不到、
> BPM `managerOID` 沒被同步更新。部門數量遠少於員工，已改成比照 `dept_level` 每次抓全部，
> 靠既有 UPSERT 邏輯處理，避免漏抓異動。

寫入順序固定：`Organization`（已存在）→ `OrganizationUnitLevel` → `OrganizationUnit` → `Users` → `Employee` → `Functions`，因為後面每一步都要用前面已寫入的 OID 當外鍵。

---

## OrganizationUnitLevel（部門層級名稱）

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | 產生 32 碼亂數 |
| `levelValue` | 104 `SORT_ORDER` |
| `organizationUnitLevelName` | 104 `LEVEL_NAME` |
| `organizationOID` | 104 `CO_ID` → 查 `Organization.OID` |
| `objectVersion` | 新增1／更新+1 |

UPSERT 判斷鍵：`levelValue` + `organizationOID`

---

## OrganizationUnit（部門）

| BPM 欄位 | 資料來源 |
|---|---|
| `OID` PK | 產生 32 碼亂數 |
| `id` | 104 `DEPT_CODE` |
| `organizationUnitName` | 104 `DEPT_NAME` |
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
| `specifiedManagerOID` | 查 `OrganizationUnit.managerOID`；**若該部門主管就是自己**，改沿 `superUnitOID` 往上層部門找「不是自己」的主管 |
| `isMain` | 固定值 1 |
| `objectVersion` | 新增1／更新+1 |

UPSERT 判斷鍵：`occupantOID` + `organizationUnitOID`
`FunctionDefinition`／`FunctionLevel` 由 BPM 管理端維護，本程式不寫入這兩張表。

> **2026-09-03 修正**：
> 1. `approvalLevelOID` 原本一律代入 `defaultLevel`，改成先比對職稱是否為主管職核決層級名稱，是則代入對應層級。
> 2. `specifiedManagerOID` 原本若員工自己是部門主管，會把自己填成自己的直屬主管；改成往上層部門找不是自己的主管。
>    ERP `gen_file.TA_GEN07`（`GetEmployeeManagerEmpNosAsync`）套用同一套邏輯。

---

## OID 產生

所有表 PK 皆為 `nchar(32)`，程式產生亂數後會查詢多張相關表確認不重複，才寫入。

## UPSERT 原則

所有表一律「先查是否存在 → 存在 UPDATE、不存在 INSERT」，沒有任何一步是刪除重建。
