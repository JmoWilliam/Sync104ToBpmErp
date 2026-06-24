# API → BPM & ERP 寫入對照表

> **專案**: Sync104ToBpmErp  
> **資料流**: 104 HR Max API → 查詢比對 → **寫入** BPM (MSSQL) & ERP (TIPTOP Oracle)  
> **API 規格來源**: `104HRMax_API欄位串接V2.17.0_20251229.xls`  
> **BPM 結構來源**: `BPM_DatabaseObject.sql`  
> **ERP 結構來源**: `TiptopDbObject_Trad.xlsx`  
> 最後更新: 2026-06-12

---

## 📡 104 HR Max API 查詢端點

| API | 說明 | 關鍵輸出欄位 |
|---|---|---|
| `GET /api/os/company` | 公司資訊 | CO_ID, CO_CODE, CO_NAME, IS_ACT |
| `GET /api/os/dept` | 部門資訊 | DEPT_ID, DEPT_CODE, DEPT_NAME, DEPT_ABBR, PARENT_DEPT_CODE, LEADER_EMP_NO, IS_ACT |
| `GET /api/os/dept_level` | 部門層級名 | DEPT_LEVEL_ID, LEVEL_NAME, SORT_ORDER |
| `GET /api/ed/emp` | 員工基本資料 | EMP_NO, EMP_NAME, DEPT1_CODE~DEPT5_CODE, JOB_CODE, GRADE_CODE, LEVEL_CODE, HIRE_DATE, QUIT_DATE... |

---

## 🗄️ 寫入對照 — BPM (MSSQL)

### BPM: OrganizationUnit (部門)
```
Database | Table Name           | ColumnName            | Description           | Source
BPM      | OrganizationUnit     | OID                   | PK, 呼叫亂數取號32碼   | (程式產生 UUID)
BPM      | OrganizationUnit     | id                    | 部門代碼               | /api/os/dept.DEPT_CODE
BPM      | OrganizationUnit     | organizationUnitName  | 部門名稱               | /api/os/dept.DEPT_NAME
BPM      | OrganizationUnit     | managerOID            | 主管 OID (FK→Users)   | /api/os/dept.LEADER_EMP_NO → Users.OID
BPM      | OrganizationUnit     | superUnitOID          | 上層部門 OID (FK→OrganizationUnit) | /api/os/dept.PARENT_DEPT_CODE → OrganizationUnit.OID
BPM      | OrganizationUnit     | objectVersion         | 物件版本號             | (遞增)
BPM      | OrganizationUnit     | organizationUnitType  | 組織單元類型           | (待確認)(這個我也不知要放什麼,但目前系統是0就先塞0吧)
BPM      | OrganizationUnit     | levelOID              | 層級 OID (FK→OrganizationUnitLevel) | /api/os/dept.DEPT_LEVEL_ID → OrganizationUnitLevel.OID
BPM      | OrganizationUnit     | organizationOID       | 所屬公司 OID (FK→Organization) | /api/os/dept.CO_ID → Organization.OID
BPM      | OrganizationUnit     | validType             | 有效狀態               | /api/os/dept.IS_ACT (1=使用中→?, 0=停用→?)
```

### BPM: OrganizationUnitLevel (部門層級名稱)
```
Database | Table Name           | ColumnName            | Description           | Source
BPM      | OrganizationUnitLevel| OID                   | PK, 呼叫亂數取號32碼   | (程式產生 UUID)
BPM      | OrganizationUnitLevel| objectVersion         | 物件版本號             | (遞增)
BPM      | OrganizationUnitLevel| levelValue            | 層級值(數字)           | /api/os/dept_level.SORT_ORDER
BPM      | OrganizationUnitLevel| organizationUnitLevelName | 層級名稱            | /api/os/dept_level.LEVEL_NAME
BPM      | OrganizationUnitLevel| organizationOID       | 所屬公司 OID           | /api/os/dept_level.CO_ID → Organization.OID
BPM      | OrganizationUnitLevel| description           | 說明                   | (空白)
```

### BPM: Organization (公司), 不需新增
```
Database | Table Name           | ColumnName            | Description           | Source
BPM      | Organization         | OID                   | PK, 呼叫亂數取號32碼   | (程式產生 UUID)
BPM      | Organization         | id                    | 公司代號               | /api/os/company.CO_CODE
BPM      | Organization         | organizationName      | 公司名稱               | /api/os/company.CO_NAME
BPM      | Organization         | objectVersion         | 物件版本號             | (遞增)
```

### BPM: Employee (員工)
```
Database | Table Name           | ColumnName            | Description           | Source
BPM      | Employee             | OID                   | PK, 呼叫亂數取號32碼   | (程式產生 UUID)
BPM      | Employee             | employeeId            | 員工編號               | /api/ed/emp.EMP_NO
BPM      | Employee             | organizationOID       | 所屬部門 OID (FK→OrganizationUnit) | /api/ed/emp.DEPT1_CODE → OrganizationUnit.OID
BPM      | Employee             | userOID               | 對應使用者 OID (FK→Users) | /api/ed/emp.EMP_NO → Users.OID
BPM      | Employee             | objectVersion         | 物件版本號             | (遞增)
BPM      | Employee             | validTo               | 有效期限               | /api/ed/emp.QUIT_DATE (離職日=失效日)
```

### BPM: Users (使用者)
```
Database | Table Name           | ColumnName            | Description           | Source
BPM      | Users                | OID                   | PK, 呼叫亂數取號32碼   | (程式產生 UUID)
BPM      | Users                | id                    | 使用者帳號             | /api/ed/emp.EMP_NO
BPM      | Users                | userName              | 使用者名稱             | /api/ed/emp.EMP_NAME
BPM      | Users                | objectVersion         | 物件版本號             | (遞增)
BPM      | Users                | password              | 密碼                   | (初始值/規則)
BPM      | Users                | leaveDate             | 離職日期               | /api/ed/emp.QUIT_DATE
BPM      | Users                | mailAddress           | 郵件地址               | /api/ed/emp.OFFICE_EMAIL
BPM      | Users                | phoneNumber           | 電話號碼               | /api/ed/emp.OFFICE_TEL
BPM      | Users                | identificationType    | 身份識別類型           | (待確認)
BPM      | Users                | localeString          | 語系                   | (預設 zh-TW)
BPM      | Users                | enableSubstitute      | 啟用代理人             | (預設 0)
BPM      | Users                | mailingFrequencyType  | 郵件頻率類型           | (預設)
BPM      | Users                | performForwardType    | 執行轉寄類型           | (預設)
BPM      | Users                | userTaskDisplay       | 任務顯示               | (預設 1)
BPM      | Users                | createdTime           | 建立時間               | (SYSDATETIME)
```

---

## 🗄️ 寫入對照 — ERP / TIPTOP (Oracle YCS)

### ERP: gem_file (部門)
```
Database | Table Name           | ColumnName            | Description           | Source
ERP      | gem_file             | GEM01                 | 部門編號 (PK)          | /api/os/dept.DEPT_CODE
ERP      | gem_file             | GEM02                 | 部門名稱               | /api/os/dept.DEPT_NAME
ERP      | gem_file             | GEM03                 | 部門全稱               | /api/os/dept.DEPT_NAME (同部門名稱, 或取 DEPT_ABBR)
ERP      | gem_file             | GEM04                 | ~~No Use~~ (保留)      | (空白)
ERP      | gem_file             | GEM05                 | 是否為會計部門          | (待確認)(抓部門生失效狀態即可)
ERP      | gem_file             | GEM06                 | ~~No Use~~ (保留)      | (空白)
ERP      | gem_file             | GEM07                 | 費用類別               | (待確認)(塞NULL)
ERP      | gem_file             | GEM08                 | ~~No Use~~ (保留)      | (空白)
ERP      | gem_file             | GEM09                 | 管理類別 (1=成本中心 2=利潤中心) | (待確認)(先塞3)
ERP      | gem_file             | GEM10                 | 對應成本中心            | (待確認)(先塞NULL)
ERP      | gem_file             | GEM11                 | ~~No Use~~ (保留)      | (空白)
ERP      | gem_file             | GEMACTI               | 資料有效碼             | /api/os/dept.IS_ACT (1→Y, 0→N)
ERP      | gem_file             | GEMUSER               | 資料所有者             | 'SYNC104'(改ttauto)
ERP      | gem_file             | GEMGRUP               | 資料所有部門           | (待確認) (A0340)
ERP      | gem_file             | GEMMODU               | 資料修改者             | 'SYNC104'(改ttauto)
ERP      | gem_file             | GEMDATE               | 最近修改日             | SYSDATE
ERP      | gem_file             | GEMORIG               | 資料建立部門           | (待確認)(TT沒這欄位)
ERP      | gem_file             | GEMORIU               | 資料建立者             | 'SYNC104'(改ttauto)
```

> **⚠ 注意**: TIPTOP `gem_file.GEM04` 在標準 DDL 中標注為 "No Use"，並非上層部門。**BPM 用 `OrganizationUnit.superUnitOID` 表達層級**。YCS schema 的 GEM_FILE DDL 中 GEM04 是 VARCHAR2(6) 標注「上層部門」，可能與標準 TIPTOP 不同，實際以 YCS schema 為準。

### ERP: geu_file (集團中心/公司) --> 不需新增
```
Database | Table Name           | ColumnName            | Description           | Source
ERP      | geu_file             | GEU00                 | 集團中心類別            | (待確認)(先塞1)
ERP      | geu_file             | GEU01                 | 集團中心代碼 (PK)       | /api/os/company.CO_CODE
ERP      | geu_file             | GEU02                 | 集團中心名稱            | /api/os/company.CO_NAME
ERP      | geu_file             | GEUACTI               | 資料有效碼             | /api/os/company.IS_ACT (1→Y, 0→N)
ERP      | geu_file             | GEUUSER               | 資料所有者             | 'SYNC104'(改ttauto)
ERP      | geu_file             | GEUGRUP               | 資料所有群             | (待確認)(先塞NULL)
ERP      | geu_file             | GEUMODU               | 資料更改者             | 'SYNC104'(改ttauto)
ERP      | geu_file             | GEUDATE               | 最近修改日             | SYSDATE
ERP      | geu_file             | GEUORIG               | 資料建立部門           | (待確認)(待確認,TT沒這欄位)
ERP      | geu_file             | GEUORIU               | 資料建立者             | 'SYNC104'(改ttauto)
```

### ERP: abd_file (部門層級)
```
Database | Table Name           | ColumnName            | Description           | Source
ERP      | abd_file             | ABD01                 | 部門編號 (PK)          | /api/os/dept.DEPT_CODE (上層部門)
ERP      | abd_file             | ABD02                 | 部門編號 (PK)          | /api/os/dept.DEPT_CODE (下層/自身部門)
ERP      | abd_file             | ABD03                 | ~~No Use~~             | (空白)
ERP      | abd_file             | ABD04                 | ~~No Use~~             | (空白)
ERP      | abd_file             | ABD05                 | ~~No Use~~             | (空白)
ERP      | abd_file             | ABD06                 | ~~No Use~~             | (空白)
ERP      | abd_file             | ABDACTI               | 資料有效碼             | 'Y'
ERP      | abd_file             | ABDUSER               | 資料所有者             | 'SYNC104'(改ttauto)
ERP      | abd_file             | ABDGRUP               | 資料所有部門           | (待確認)(A0340)
ERP      | abd_file             | ABDMODU               | 資料修改者             | 'SYNC104'(改ttauto)
ERP      | abd_file             | ABDDATE               | 最近修改日             | SYSDATE
ERP      | abd_file             | ABDORIG               | 資料建立部門           | (待確認)(A0340)
ERP      | abd_file             | ABDORIU               | 資料建立者             | 'SYNC104'(改ttauto)
```

> **ABD_FILE 層級邏輯**: `abd01` = 父層部門, `abd02` = 子層部門。同一筆記錄表示 `abd01` 是 `abd02` 的父層。頂層部門需插入一筆 `abd01 = abd02 = 自己`。

### ERP: gen_file (員工資料檔)
```
Database | Table Name           | ColumnName            | Description           | Source
ERP      | gen_file             | GEN01                 | 員工編號               | /api/ed/emp.EMP_NO
ERP      | gen_file             | GEN02                 | 員工姓名               | /api/ed/emp.EMP_NAME
ERP      | gen_file             | GEN03                 | 部門代號               | (待確認)(/api/ed/emp/DEPT1_CODE)
ERP      | gen_file             | GEN04                 | 職稱                   | (待確認)(/api/ed/emp/DEPT1_NAME)
ERP      | gen_file             | GEN05                 | 不確定                 | (待確認)(/api/ed/emp/OFFICE_TEL)
ERP      | gen_file             | GEN06                 | 不確定                 | (待確認)(/api/ed/emp/OFFICE_EMAIL)
ERP      | gen_file             | GENACTI               | 資料有效碼             | 'Y'
ERP      | gen_file             | GENUSER               | 資料所有者             | 'SYNC104'(改ttauto)
ERP      | gen_file             | GENGRUP               | 資料所有群             | (待確認)(A0340)
ERP      | gen_file             | GENMODU               | 資料修改者             | 'SYNC104'(改ttauto)
ERP      | gen_file             | GENDATE               | 最近修改日             | SYSDATE
ERP      | gen_file             | TA_GEN07              | 擴充欄位               | (待確認)(塞NULL)
ERP      | gen_file             | TA_GEN08              | 擴充欄位               | (待確認)(塞NULL)
```

---

## 🔗 關聯套疊邏輯

```
104 /api/os/company ──────────────────┐
                                      ├──→ BPM Organization
                                      └──→ ERP geu_file

104 /api/os/dept ─────────────────────┐
                                      ├──→ BPM OrganizationUnit (部門單元)
                                      │      └── superUnitOID → 上層部門 (自參照樹)
                                      ├──→ BPM OrganizationUnitLevel (層級名稱)
                                      └──→ ERP gem_file (部門基本資料)
                                            └── abd_file (部門層級關係: abd01=父, abd02=子)

104 /api/ed/emp ──────────────────────┐
                                      ├──→ BPM Employee
                                      ├──→ BPM Users --> 怎麼新增, 和Employee一起新增嗎?
                                      
                                      └──→ ERP gen_file
```
