using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// BPM (MS-SQL) 資料庫服務
    /// 對照表: api_erp_bpm_mapping.md
    ///
    /// 寫入的 Table:
    ///   - OrganizationUnit      : 部門 (INSERT/UPDATE)
    ///   - Users                 : 系統使用者 (INSERT/UPDATE)
    ///   - Employee              : 員工歸屬 (INSERT/UPDATE)
    ///   - Functions             : 職稱/簽核歸屬 (INSERT/UPDATE，2026-09-03 新增)
    ///
    /// 不寫入的 Table (已在 BPM 管理端建立，只查詢比對):
    ///   - Organization          : 公司
    ///   - OrganizationUnitLevel : 部門層級名稱 (2026-09-07 起停用寫入，客戶反映不該被程式修改)
    ///   - FunctionDefinition    : 職務定義 (職稱清單)
    ///   - FunctionLevel         : 職務核決層級定義
    /// </summary>
    public class BpmDatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private readonly ILoggerService _logger;
        private readonly int _batchSize;
        private readonly string _defaultPassword;

        public BpmDatabaseService(DatabaseSettings settings, ILoggerService logger, int batchSize = 100, string defaultPassword = "0000")
        {
            _connectionString = settings.ConnectionString;
            _logger = logger;
            _batchSize = batchSize;
            _defaultPassword = defaultPassword;
        }

        public string GetDatabaseName() => "BPM (MS-SQL)";

        private IDbConnection CreateConnection() => new SqlConnection(_connectionString);

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var connection = CreateConnection();
                connection.Open();
                _logger.LogDbConnection(GetDatabaseName(), true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDbConnection(GetDatabaseName(), false, ex.Message);
                return false;
            }
        }

        #region 拓撲排序

        /// <summary>
        /// 對部門清單做拓撲排序，確保父部門永遠排在子部門前面。
        /// 這樣同步時子部門查詢 superUnitOID 才能找到已存在的父部門。
        /// </summary>
        private static List<Department> TopologicalSortDepartments(List<Department> departments)
        {
            var deptMap = departments.ToDictionary(d => d.DeptCode, d => d);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Department>(departments.Count);

            void Visit(Department dept)
            {
                if (visited.Contains(dept.DeptCode)) return;
                visited.Add(dept.DeptCode);

                // 先遞迴處理父部門（若父部門也在清單中且不是自己）
                if (!string.IsNullOrEmpty(dept.ParentDeptCode)
                    && !dept.ParentDeptCode.Equals(dept.DeptCode, StringComparison.OrdinalIgnoreCase)
                    && deptMap.TryGetValue(dept.ParentDeptCode, out var parent))
                {
                    Visit(parent);
                }

                result.Add(dept);
            }

            foreach (var dept in departments)
                Visit(dept);

            return result;
        }

        #endregion

        #region OID Helper

        private async Task<bool> OIDExistsAsync(IDbConnection connection, IDbTransaction transaction, string tableName, string oid)
        {
            var count = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(1) FROM [{tableName}] WHERE [OID] = @OID",
                new { OID = oid },
                transaction);
            return count > 0;
        }

        private async Task<string> GenerateUniqueOIDAsync(IDbConnection connection, IDbTransaction transaction, params string[] tables)
        {
            return await OidHelper.GenerateUniqueAsync(async (oid) =>
            {
                foreach (var table in tables)
                {
                    if (await OIDExistsAsync(connection, transaction, table, oid))
                        return true;
                }
                return false;
            });
        }

        #endregion

        #region Organization（公司）— 不需寫入

        /// <summary>
        /// Organization 已在 BPM 管理端建立，不需從 104 同步。
        /// </summary>
        public Task<SyncResult> SyncOrganizationAsync(List<CompanyInfo> companies)
        {
            _logger.Info("[BPM] Organization 不需從 104 同步 (已在 BPM 管理端建立)，跳過");
            return Task.FromResult(new SyncResult { DataType = "Organization", TargetSystem = "BPM(跳過)" });
        }

        /// <summary>
        /// 輔助: 依 CompanyCode 查詢 Organization.OID
        /// </summary>
        private async Task<string?> GetOrganizationOIDAsync(IDbConnection connection, IDbTransaction transaction, string companyCode)
        {
            return await connection.QueryFirstOrDefaultAsync<string>(
                "SELECT [OID] FROM [Organization] WHERE [id] = @CoCode",
                new { CoCode = companyCode },
                transaction);
        }

        #endregion

        #region OrganizationUnit（部門）

        public async Task<SyncResult> SyncOrganizationUnitsAsync(List<Department> departments, long coId, string coCode)
        {
            var result = new SyncResult { DataType = "OrganizationUnit", TargetSystem = "BPM" };
            if (departments == null || departments.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                // 查詢 Organization.OID（公司）
                var organizationOID = await GetOrganizationOIDAsync(connection, transaction, coCode);
                if (string.IsNullOrEmpty(organizationOID))
                {
                    _logger.Warning($"[BPM] 找不到 Organization (CO_CODE={coCode})，部門將不帶 organizationOID");
                }

                // ── 批量預載入查詢對照表（避免 N+1 問題） ──
                var userOidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in await connection.QueryAsync(
                    "SELECT [id], [OID] FROM [Users]", transaction: transaction))
                    userOidMap[(string)row.id] = (string)row.OID;

                // orgUnitOidMap 在迴圈中會即時更新（新 INSERT 後加入），
                // 配合拓撲排序確保父部門先寫入再被子部門查詢
                var orgUnitOidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in await connection.QueryAsync(
                    "SELECT [id], [OID] FROM [OrganizationUnit]", transaction: transaction))
                    orgUnitOidMap[(string)row.id] = (string)row.OID;

                // 2026-09-11 修正：原本用 104 DEPT_LEVEL_ID 對照 OrganizationUnitLevel.levelValue，
                // 但兩者是完全不同的數字空間 (DEPT_LEVEL_ID 是 104 內部的大範圍任意 ID，
                // levelValue 是各公司自己由 0 開始編的小範圍序號，兩家公司階層數還不一樣)，
                // 這個比對事實上永遠對不上，導致 levelOID 永遠是 NULL——正式資料庫還原比對後才發現
                // 這個問題原本就存在，且會把既有正確的 levelOID 覆蓋成 NULL。
                // 改用「層級名稱」(104 DEPT_LEVEL_NAME ↔ BPM organizationUnitLevelName) 比對，
                // 名稱才是兩邊真正對得上的欄位。
                var levelOidMap = new Dictionary<(string, string?), string>();
                foreach (var row in await connection.QueryAsync(
                    "SELECT [organizationUnitLevelName], [organizationOID], [OID] FROM [OrganizationUnitLevel]",
                    transaction: transaction))
                {
                    string? levelName = ((string?)row.organizationUnitLevelName)?.Trim();
                    if (string.IsNullOrEmpty(levelName)) continue;
                    levelOidMap[(levelName, (string?)row.organizationOID)] = (string)row.OID;
                }

                // ── 拓撲排序：確保父部門排在子部門前 ──
                var sorted = TopologicalSortDepartments(departments);

                result.TotalCount = sorted.Count;
                int processedCount = 0;

                foreach (var dept in sorted)
                {
                    processedCount++;
                    try
                    {
                        // ── 查詢關聯 OID（全部用記憶體對照表，無額外 DB 查詢） ──
                        orgUnitOidMap.TryGetValue(dept.DeptCode, out var existingOid);

                        // 2026-09-09 修正：104 DEPT_NAME 常常會把部門代碼帶在名稱開頭
                        // (例如 "A0450業務行政部")，寫進 BPM 前先去掉這個前綴，只留「業務行政部」。
                        string orgUnitName = dept.DeptName ?? "";
                        if (!string.IsNullOrEmpty(dept.DeptCode) &&
                            orgUnitName.StartsWith(dept.DeptCode, StringComparison.OrdinalIgnoreCase))
                        {
                            orgUnitName = orgUnitName.Substring(dept.DeptCode.Length);
                        }

                        string? managerOID = null;
                        if (!string.IsNullOrEmpty(dept.LeaderEmpNo))
                            userOidMap.TryGetValue(dept.LeaderEmpNo, out managerOID);

                        string? superUnitOID = null;
                        if (!string.IsNullOrEmpty(dept.ParentDeptCode))
                            orgUnitOidMap.TryGetValue(dept.ParentDeptCode, out superUnitOID);

                        string? levelOID = null;
                        if (!string.IsNullOrEmpty(dept.DeptLevelName))
                            levelOidMap.TryGetValue((dept.DeptLevelName.Trim(), organizationOID), out levelOID);

                        if (!string.IsNullOrEmpty(existingOid))
                        {
                            // ── 資料已存在 — UPDATE (2026-07-16 依客戶回覆 DataMappingUserConfirm20260716.xlsx 啟用)
                            //    僅更新客戶確認需要異動的欄位: organizationUnitName, managerOID, superUnitOID, levelOID, validType
                            //    organizationOID 客戶未勾選需要更新 (理論上部門不會換公司)，維持原值不動 ──
                            await connection.ExecuteAsync(@"
                                UPDATE [OrganizationUnit] SET
                                    [organizationUnitName] = @OrgUnitName,
                                    [managerOID]           = @ManagerOID,
                                    [superUnitOID]         = @SuperUnitOID,
                                    [levelOID]             = @LevelOID,
                                    [validType]            = @ValidType,
                                    [objectVersion]        = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = existingOid,
                                    OrgUnitName = orgUnitName,
                                    ManagerOID = (object?)managerOID ?? DBNull.Value,
                                    SuperUnitOID = (object?)superUnitOID ?? DBNull.Value,
                                    LevelOID = (object?)levelOID ?? DBNull.Value,
                                    ValidType = dept.IsAct == 1 ? 1 : 0
                                },
                                transaction);

                            result.SuccessCount++;
                            _logger.LogSyncDetail("OrganizationUnit", "UPDATE", dept.DeptCode, true);
                            _logger.LogSyncRecord("OrganizationUnit",
                                $"OID={existingOid}, id={dept.DeptCode}, organizationUnitName={orgUnitName}, " +
                                $"managerOID={managerOID ?? "NULL"}, superUnitOID={superUnitOID ?? "NULL"}, " +
                                $"levelOID={levelOID ?? "NULL"}, validType={(dept.IsAct == 1 ? 1 : 0)} (UPDATE)");
                        }
                        else
                        {
                            // ── Insert ──
                            var oid = await GenerateUniqueOIDAsync(connection, transaction,
                                "OrganizationUnit", "Organization", "OrganizationUnitLevel", "Employee", "Users");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [OrganizationUnit] (
                                    [OID], [id], [organizationUnitName], [managerOID],
                                    [superUnitOID], [objectVersion], [organizationUnitType],
                                    [levelOID], [organizationOID], [validType]
                                ) VALUES (
                                    @OID, @Id, @OrgUnitName, @ManagerOID,
                                    @SuperUnitOID, 1,
                                    --待確認 organizationUnitType 對應104什麼值
                                    1,
                                    @LevelOID, @OrganizationOID, @ValidType
                                )",
                                new
                                {
                                    OID = oid,
                                    Id = dept.DeptCode,
                                    OrgUnitName = orgUnitName,
                                    ManagerOID = (object?)managerOID ?? DBNull.Value,
                                    SuperUnitOID = (object?)superUnitOID ?? DBNull.Value,
                                    LevelOID = (object?)levelOID ?? DBNull.Value,
                                    OrganizationOID = (object?)organizationOID ?? DBNull.Value,
                                    ValidType = dept.IsAct == 1 ? 1 : 0
                                },
                                transaction);

                            // 新插入的部門加入對照表，供後續子部門查詢 superUnitOID
                            orgUnitOidMap[dept.DeptCode] = oid;
                            _logger.LogSyncDetail("OrganizationUnit", "INSERT", dept.DeptCode, true);
                            _logger.LogSyncRecord("OrganizationUnit",
                                $"OID={oid}, id={dept.DeptCode}, organizationUnitName={orgUnitName}, " +
                                $"managerOID={managerOID ?? "NULL"}, superUnitOID={superUnitOID ?? "NULL"}, " +
                                $"levelOID={levelOID ?? "NULL"}, organizationOID={organizationOID ?? "NULL"}, " +
                                $"objectVersion=1, organizationUnitType=1, validType={(dept.IsAct == 1 ? 1 : 0)}");
                            result.SuccessCount++;
                        }

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 部門同步進度: {processedCount}/{sorted.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"部門 {dept.DeptCode} ({dept.DeptName}): {ex.Message}");
                        _logger.LogSyncDetail("OrganizationUnit", "SYNC", dept.DeptCode, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"OrganizationUnit ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 OrganizationUnit 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }

        #endregion

        #region OrganizationUnitLevel（部門層級名稱）

        /// <summary>
        /// 2026-09-07 停用：客戶反映 OrganizationUnitLevel 這張表的資料不該被我們的程式修改
        /// (部門層級名稱/核決層級定義屬於 BPM 管理端維護的基礎設定)。
        /// 原本每次同步都會對既有資料做 UPDATE (即使值沒變也會 objectVersion+1)，
        /// 導致這幾筆固定的層級資料版本號被無意義地一直往上累加 (實測其中一筆被同步跑到 objectVersion=58)。
        /// 改為完全不寫入，只保留查詢比對用途；OrganizationUnit.levelOID 的對照表
        /// (SyncOrganizationUnitsAsync 裡的 levelOidMap) 仍直接查詢這張表既有資料，不受影響。
        /// 原本的 UPSERT 邏輯整段註解保留在下方，之後如果要恢復可以直接取消註解。
        /// </summary>
        public Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId, string coCode)
        {
            _logger.Info("[BPM] OrganizationUnitLevel 不寫入 (2026-09-07 起改為僅查詢比對，資料由 BPM 管理端維護)，跳過");
            return Task.FromResult(new SyncResult { DataType = "OrganizationUnitLevel", TargetSystem = "BPM(跳過)" });
        }

        /*
        public async Task<SyncResult> SyncOrganizationUnitLevelsAsync_Disabled(List<DeptHierarchy> hierarchy, long coId, string coCode)
        {
            var result = new SyncResult { DataType = "OrganizationUnitLevel", TargetSystem = "BPM" };
            if (hierarchy == null || hierarchy.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                // OrganizationUnitLevel.organizationOID 對應到 Organization.OID，
                // Organization.id = CO_CODE，由呼叫端 (SyncService) 傳入 coCode 查詢
                string? organizationOID = await GetOrganizationOIDAsync(connection, transaction, coCode);
                if (string.IsNullOrEmpty(organizationOID))
                {
                    _logger.Warning($"[BPM] 找不到 Organization (CO_CODE={coCode})，層級將不帶 organizationOID");
                }

                result.TotalCount = hierarchy.Count;
                int processedCount = 0;

                foreach (var item in hierarchy)
                {
                    processedCount++;
                    try
                    {
                        int levelValue = (int)(item.SortOrder ?? 0);

                        // 查詢已存在的 OID (by levelValue + organizationOID)
                        var existingOid = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [OrganizationUnitLevel] WHERE [levelValue] = @LevelValue AND [organizationOID] = @OrgOID",
                            new { LevelValue = levelValue, OrgOID = organizationOID ?? (object)DBNull.Value },
                            transaction);

                        if (!string.IsNullOrEmpty(existingOid))
                        {
                            // ── 資料已存在 — UPDATE (2026-07-16 依客戶回覆啟用)
                            //    僅更新 organizationUnitLevelName；description 客戶雖勾選需要更新，
                            //    但目前完全沒有 104 資料來源可供寫入 (現行也還是 NULL)，
                            //    貿然 UPDATE 只會覆蓋成 NULL、可能誤刪既有人工填寫的內容，故暫不啟用，
                            //    待客戶提供 description 的資料來源後再補上 ──
                            await connection.ExecuteAsync(@"
                                UPDATE [OrganizationUnitLevel] SET
                                    [organizationUnitLevelName] = @LevelName,
                                    [objectVersion] = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new { OID = existingOid, LevelName = item.LevelName },
                                transaction);

                            result.SuccessCount++;
                            _logger.LogSyncDetail("OrganizationUnitLevel", "UPDATE", item.LevelName, true);
                            _logger.LogSyncRecord("OrganizationUnitLevel",
                                $"OID={existingOid}, organizationUnitLevelName={item.LevelName} (UPDATE)");
                        }
                        else
                        {
                            // ── Insert ──
                            var oid = await GenerateUniqueOIDAsync(connection, transaction,
                                "OrganizationUnitLevel", "OrganizationUnit", "Organization", "Employee", "Users");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [OrganizationUnitLevel] (
                                    [OID], [objectVersion], [levelValue],
                                    [organizationUnitLevelName], [organizationOID],
                                    [description]
                                ) VALUES (
                                    @OID, 1, @LevelValue,
                                    @LevelName, @OrganizationOID,
                                    --待確認 description
                                    NULL
                                )",
                                new
                                {
                                    OID = oid,
                                    LevelValue = levelValue,
                                    LevelName = item.LevelName,
                                    OrganizationOID = (object?)organizationOID ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("OrganizationUnitLevel", "INSERT", item.LevelName, true);
                            _logger.LogSyncRecord("OrganizationUnitLevel",
                                $"OID={oid}, objectVersion=1, levelValue={levelValue}, " +
                                $"organizationUnitLevelName={item.LevelName}, organizationOID={organizationOID ?? "NULL"}");
                            result.SuccessCount++;
                        }

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 層級同步進度: {processedCount}/{hierarchy.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"層級 {item.LevelName}: {ex.Message}");
                        _logger.LogSyncDetail("OrganizationUnitLevel", "SYNC", item.LevelName, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"OrganizationUnitLevel ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 OrganizationUnitLevel 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }
        */

        #endregion

        #region Users + Employee（員工）

        public async Task<SyncResult> SyncEmployeesAsync(List<Employee> employees, long coId)
        {
            var result = new SyncResult { DataType = "Employee", TargetSystem = "BPM" };
            if (employees == null || employees.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                result.TotalCount = employees.Count;
                int processedCount = 0;

                foreach (var emp in employees)
                {
                    processedCount++;
                    try
                    {
                        // ═══════════════════════════════════
                        // 1. Users (使用者) — UPSERT
                        // ═══════════════════════════════════
                        var userOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Users] WHERE [id] = @Id",
                            new { Id = emp.EmpNo },
                            transaction);

                        bool userInserted = false;
                        bool userUpdated = false;
                        if (!string.IsNullOrEmpty(userOID))
                        {
                            // ── 資料已存在 — UPDATE (2026-07-16 依客戶回覆 DataMappingUserConfirm20260716.xlsx 啟用)
                            //    僅更新客戶確認需要異動的欄位: userName, mailAddress, leaveDate
                            //    phoneNumber、password 客戶未勾選需要更新，維持原值不動 ──
                            await connection.ExecuteAsync(@"
                                UPDATE [Users] SET
                                    [userName]          = @UserName,
                                    [mailAddress]       = @MailAddress,
                                    [leaveDate]         = @LeaveDate,
                                    [objectVersion]     = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = userOID,
                                    UserName = emp.EmpName,
                                    MailAddress = (object?)emp.Email ?? DBNull.Value,
                                    LeaveDate = (object?)emp.QuitDate ?? DBNull.Value
                                },
                                transaction);

                            userUpdated = true;
                            _logger.LogSyncDetail("Users", "UPDATE", emp.EmpNo, true);
                            _logger.LogSyncRecord("Users",
                                $"OID={userOID}, id={emp.EmpNo}, userName={emp.EmpName}, " +
                                $"mailAddress={emp.Email ?? "NULL"}, leaveDate={emp.QuitDate?.ToString("yyyy-MM-dd") ?? "NULL"} (UPDATE)");
                        }
                        else
                        {
                            // ── Insert Users ──
                            // 2026-07-16 依客戶回覆新增 ldapid = 104 EMP_EN_NAME (英文姓名)
                            //
                            // 2026-09-03 對全庫 1722 筆 Users 做欄位分佈健檢後修正以下預設值
                            // （修正前這幾個欄位的預設值只有我們自己新增的那幾筆是這樣，全庫其餘紀錄一致是另一個值）：
                            //   identificationType : 'Employee' → 'DEFAULT'（全庫 1710/1710 既有紀錄皆為 DEFAULT，
                            //                         懷疑 BPM 前端「模擬使用者」查詢頁就是靠這個欄位判斷是否為正常帳號，
                            //                         導致新同步進去的員工姓名在該頁面顯示空白）
                            //   enableSubstitute   : 0 → 1（全庫 1709/1722 為 1）
                            //   performForwardType : 0 → 2（全庫 1707/1722 為 2）
                            //   passwordWrongTimes : 未寫入(NULL) → 0（全庫 1710/1710 既有紀錄皆為 0，本次新增此欄位）
                            // currentType / traceWorkStatus 全庫既有資料本身就有兩種以上的值混用，找不到明顯多數，
                            // 暫不寫入(維持 NULL)，待確認實際用途後再補。
                            // password 目前仍是明碼預設值，既有帳號的 password 欄位都是加密過的密文，
                            // 這點需要跟 BPM 管理員/廠商確認密碼要用什麼方式產生，暫不處理。
                            userOID = await GenerateUniqueOIDAsync(connection, transaction,
                                "Users", "Employee", "OrganizationUnit", "Organization", "OrganizationUnitLevel");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [Users] (
                                    [OID], [id], [userName], [objectVersion], [password],
                                    [leaveDate], [mailAddress], [localeString], [phoneNumber],
                                    [identificationType],
                                    [enableSubstitute], [mailingFrequencyType],
                                    [performForwardType], [userTaskDisplay], [createdTime],
                                    [ldapid], [passwordWrongTimes]
                                ) VALUES (
                                    @OID, @Id, @UserName, 1, @Password,
                                    @LeaveDate, @MailAddress, 'zh_TW', @Phone,
                                    'DEFAULT',
                                    1, 0,
                                    2, 1, SYSDATETIME(),
                                    @LdapId, 0
                                )",
                                new
                                {
                                    OID = userOID,
                                    Id = emp.EmpNo,
                                    UserName = emp.EmpName,
                                    Password = _defaultPassword,
                                    LeaveDate = (object?)emp.QuitDate ?? DBNull.Value,
                                    MailAddress = (object?)emp.Email ?? DBNull.Value,
                                    Phone = (object?)emp.Phone ?? DBNull.Value,
                                    LdapId = (object?)emp.EmpNameEn ?? DBNull.Value
                                },
                                transaction);

                            userInserted = true;
                            _logger.LogSyncDetail("Users", "INSERT", emp.EmpNo, true);
                            _logger.LogSyncRecord("Users",
                                $"OID={userOID}, id={emp.EmpNo}, userName={emp.EmpName}, objectVersion=1, " +
                                $"mailAddress={emp.Email ?? "NULL"}, phoneNumber={emp.Phone ?? "NULL"}, " +
                                $"leaveDate={emp.QuitDate?.ToString("yyyy-MM-dd") ?? "NULL"}, ldapid={emp.EmpNameEn ?? "NULL"}");
                        }

                        // ═══════════════════════════════════
                        // 2. 查詢 organizationOID（公司，非部門）
                        //    2026-09-03 資料庫健檢發現：BPM 既有 1705 筆 Employee 中，
                        //    1689 筆 (99%) 的 organizationOID 指向 Organization(公司).OID，
                        //    只有本次新同步的 16 筆指向 OrganizationUnit(部門).OID，
                        //    與既有慣例不符。改回 CO_CODE → Organization.OID，
                        //    部門/職稱/簽核歸屬待 Functions 表建置後再處理 (見 BPM_TableSchema.xlsx)。
                        // ═══════════════════════════════════
                        string? organizationOID = await GetOrganizationOIDAsync(connection, transaction, emp.CompanyCode);
                        if (string.IsNullOrEmpty(organizationOID))
                        {
                            _logger.Warning($"[BPM] 找不到 Organization (CO_CODE={emp.CompanyCode})，員工 {emp.EmpNo} 的 organizationOID 將為 NULL");
                        }

                        // ═══════════════════════════════════
                        // 3. Employee（員工歸屬）— UPSERT
                        // 2026-09-11 修正：原本只用 employeeId 查詢比對鍵，但 organizationOID 改成
                        // 公司層級後，同一個人可能合法擁有「多筆」Employee (一家公司一筆，例如集團
                        // 董事長橫跨多家子公司)，只用 employeeId 查詢在這種情況下會隨機比對到某一筆
                        // (不一定是這次要處理的公司)，導致重複新增或誤改到別家公司的記錄。
                        // 改成 employeeId + organizationOID 一起當比對鍵，才能正確對應「這個人在這家公司」的那一筆。
                        // ═══════════════════════════════════
                        var empOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Employee] WHERE [employeeId] = @EmpNo AND [organizationOID] = @OrganizationOID",
                            new { EmpNo = emp.EmpNo, OrganizationOID = (object?)organizationOID ?? DBNull.Value },
                            transaction);

                        bool empInserted = false;
                        bool empUpdated = false;
                        if (!string.IsNullOrEmpty(empOID))
                        {
                            // ── 資料已存在 — UPDATE (2026-07-16 依客戶回覆啟用)
                            //    僅更新客戶確認需要異動的欄位: organizationOID (改公司，2026-09-03 修正)、validTo (離職日)
                            //    userOID 客戶備註「系統關聯鍵，理論上不應變動」，維持原值不動 ──
                            await connection.ExecuteAsync(@"
                                UPDATE [Employee] SET
                                    [organizationOID] = @OrganizationOID,
                                    [objectVersion]    = [objectVersion] + 1,
                                    [validTo]          = @ValidTo
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = empOID,
                                    OrganizationOID = (object?)organizationOID ?? DBNull.Value,
                                    ValidTo = (object?)emp.QuitDate ?? DBNull.Value
                                },
                                transaction);

                            empUpdated = true;
                            _logger.LogSyncDetail("Employee", "UPDATE", emp.EmpNo, true);
                            _logger.LogSyncRecord("Employee",
                                $"OID={empOID}, employeeId={emp.EmpNo}, organizationOID={organizationOID ?? "NULL"}, " +
                                $"validTo={emp.QuitDate?.ToString("yyyy-MM-dd") ?? "NULL"} (UPDATE)");
                        }
                        else
                        {
                            // ── Insert Employee ──
                            empOID = await GenerateUniqueOIDAsync(connection, transaction,
                                "Employee", "Users", "OrganizationUnit", "Organization", "OrganizationUnitLevel");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [Employee] (
                                    [OID], [employeeId], [organizationOID],
                                    [userOID], [objectVersion], [validTo]
                                ) VALUES (
                                    @OID, @EmpNo, @OrganizationOID,
                                    @UserOID, 1, @ValidTo
                                )",
                                new
                                {
                                    OID = empOID,
                                    EmpNo = emp.EmpNo,
                                    OrganizationOID = (object?)organizationOID ?? DBNull.Value,
                                    UserOID = userOID,
                                    ValidTo = (object?)emp.QuitDate ?? DBNull.Value
                                },
                                transaction);

                            empInserted = true;
                            _logger.LogSyncDetail("Employee", "INSERT", emp.EmpNo, true);
                            _logger.LogSyncRecord("Employee",
                                $"OID={empOID}, employeeId={emp.EmpNo}, organizationOID={organizationOID ?? "NULL"}, " +
                                $"userOID={userOID}, objectVersion=1, validTo={emp.QuitDate?.ToString("yyyy-MM-dd") ?? "NULL"}");
                        }

                        // 此筆員工只要 Users 或 Employee 任一筆有新增/更新即視為成功處理
                        if (userInserted || empInserted || userUpdated || empUpdated)
                            result.SuccessCount++;
                        else
                            result.SkippedCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 員工同步進度: {processedCount}/{employees.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"員工 {emp.EmpNo} ({emp.EmpName}): {ex.Message}");
                        _logger.LogSyncDetail("Employee", "SYNC", emp.EmpNo, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"Employee+Users ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 Employee+Users 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }

        #endregion

        #region 主管工號查詢 (供 ERP gen_file.TA_GEN07 使用)

        /// <summary>
        /// 查詢每位員工所屬部門主管 (OrganizationUnit.managerOID) 對應的
        /// 主管員工編號 (Users.id)，供 ERP gen_file.TA_GEN07「直屬主管工號」使用。
        /// 2026-09-03 修正1: 改以 104 Dept1Code 直接查 OrganizationUnit，不再經由 Employee.organizationOID
        /// (該欄位已改回指向 Organization 公司層級，不再是部門)。
        /// 2026-09-03 修正2: 若員工本身就是所屬部門的主管，直屬主管不該是自己，改沿 superUnitOID
        /// 往上層部門找「不是自己」的主管。部門表筆數遠小於員工，整表載入記憶體後直接往上走，
        /// 避免對每位員工各自查一次 DB。
        /// </summary>
        public async Task<Dictionary<string, string>> GetEmployeeManagerEmpNosAsync(List<Employee> employees)
        {
            var managerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (employees == null || employees.Count == 0) return managerMap;

            using var connection = CreateConnection();
            connection.Open();

            // 部門表整批載入 (筆數遠小於員工)，方便沿 superUnitOID 往上層找主管
            var deptRows = await connection.QueryAsync<(string OID, string Id, string? ManagerOID, string? SuperUnitOID)>(
                "SELECT [OID], [id], [managerOID], [superUnitOID] FROM [OrganizationUnit]");
            var deptByCode = new Dictionary<string, (string OID, string? ManagerOID, string? SuperUnitOID)>(StringComparer.OrdinalIgnoreCase);
            var deptByOID = new Dictionary<string, (string? ManagerOID, string? SuperUnitOID)>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in deptRows)
            {
                deptByCode[d.Id] = (d.OID, d.ManagerOID, d.SuperUnitOID);
                deptByOID[d.OID] = (d.ManagerOID, d.SuperUnitOID);
            }

            // Users.id <-> OID 對照，供 selfOID 比對與最終轉回工號使用
            var userRows = await connection.QueryAsync<(string OID, string Id)>("SELECT [OID], [id] FROM [Users]");
            var userOidById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var userIdByOid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in userRows)
            {
                userOidById[u.Id] = u.OID;
                userIdByOid[u.OID] = u.Id;
            }

            string? ResolveNonSelfManagerOID(string deptCode, string selfUserOID)
            {
                if (!deptByCode.TryGetValue(deptCode, out var dept)) return null;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var currentOID = dept.OID;
                (string? ManagerOID, string? SuperUnitOID) current = (dept.ManagerOID, dept.SuperUnitOID);

                for (int depth = 0; depth < 20 && !string.IsNullOrEmpty(currentOID); depth++)
                {
                    if (!visited.Add(currentOID)) break;

                    if (!string.IsNullOrEmpty(current.ManagerOID) &&
                        !string.Equals(current.ManagerOID, selfUserOID, StringComparison.OrdinalIgnoreCase))
                    {
                        return current.ManagerOID;
                    }

                    if (string.IsNullOrEmpty(current.SuperUnitOID) || !deptByOID.TryGetValue(current.SuperUnitOID, out var parent))
                        break;

                    currentOID = current.SuperUnitOID;
                    current = parent;
                }

                return null;
            }

            foreach (var emp in employees)
            {
                if (string.IsNullOrEmpty(emp.Dept1Code)) continue;
                if (!userOidById.TryGetValue(emp.EmpNo, out var selfOID)) continue;
                if (!deptByCode.TryGetValue(emp.Dept1Code, out var dept)) continue;

                var mgrOID = dept.ManagerOID;
                if (!string.IsNullOrEmpty(mgrOID) && string.Equals(mgrOID, selfOID, StringComparison.OrdinalIgnoreCase))
                {
                    // 自己是部門主管，往上層部門找不是自己的主管；
                    // 2026-09-09 修正：一路找到組織最頂層都找不到 (例如董事長，上面已經沒有部門)，
                    // 維持自己是自己的直屬主管，不要留空——這也是 BPM 既有資料在頂層的慣例寫法，
                    // 留空反而是異常值。
                    var resolved = ResolveNonSelfManagerOID(emp.Dept1Code, selfOID);
                    mgrOID = !string.IsNullOrEmpty(resolved) ? resolved : selfOID;
                }

                if (!string.IsNullOrEmpty(mgrOID) && userIdByOid.TryGetValue(mgrOID, out var mgrEmpNo))
                    managerMap[emp.EmpNo] = mgrEmpNo;
            }

            return managerMap;
        }

        #endregion

        #region Functions（組織單元職務 — 職稱/簽核歸屬）

        /// <summary>
        /// 同步員工的職稱/簽核歸屬到 BPM Functions 表。
        /// 2026-09-03 新增：對正式 BPM DB 查證後確認 —
        ///   - Functions.occupantOID / specifiedManagerOID 都是指向 Users.OID（不是 Employee.OID）
        ///   - FunctionDefinition (517筆) / FunctionLevel 由 BPM 端既有資料維護，本方法僅查詢比對，
        ///     不自動新增；查無對應職稱時記錄警告並跳過該員工，不中斷其他人
        ///   - approvalLevelOID 採用 FunctionLevel.functionLevelName = 'defaultLevel'
        ///     （全庫實測 1827 筆 Functions 中有 1401 筆 / 77% 是這個值，其餘為主管職專屬層級，
        ///     細分規則待客戶確認後再補）；查無 defaultLevel 時該欄位留 NULL（欄位允許 NULL）
        ///   - specifiedManagerOID 沿用 OrganizationUnit.managerOID（跟 gen_file.TA_GEN07 用同一份部門主管資料）
        /// 需在 SyncEmployeesAsync (Users+Employee) 之後執行，才能查到 occupantOID。
        /// UPSERT 判斷鍵: occupantOID + organizationUnitOID（同一人同一部門只保留一筆；
        /// 既有的其他部門兼職 Functions 記錄，因為 key 不吻合不會被異動）
        /// </summary>
        public async Task<SyncResult> SyncEmployeeFunctionsAsync(List<Employee> employees, long coId)
        {
            var result = new SyncResult { DataType = "Functions", TargetSystem = "BPM" };
            if (employees == null || employees.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                result.TotalCount = employees.Count;
                int processedCount = 0;

                foreach (var emp in employees)
                {
                    processedCount++;
                    try
                    {
                        // 1. 公司 OID（供 FunctionDefinition / FunctionLevel 查詢範圍使用）
                        string? companyOID = await GetOrganizationOIDAsync(connection, transaction, emp.CompanyCode);
                        if (string.IsNullOrEmpty(companyOID))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions 略過 {emp.EmpNo}: 找不到 Organization (CO_CODE={emp.CompanyCode})");
                            continue;
                        }

                        // 2. occupantOID = Users.OID
                        string? occupantOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Users] WHERE [id] = @EmpNo",
                            new { emp.EmpNo },
                            transaction);
                        if (string.IsNullOrEmpty(occupantOID))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions 略過 {emp.EmpNo}: 找不到對應 Users 記錄（應已在 Users 同步階段建立）");
                            continue;
                        }

                        // 3. organizationUnitOID（部門）+ specifiedManagerOID（部門主管，來自 OrganizationUnit.managerOID）
                        if (string.IsNullOrEmpty(emp.Dept1Code))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions 略過 {emp.EmpNo}: 104 未回傳部門代碼 (Dept1Code)");
                            continue;
                        }

                        var deptRow = await connection.QueryFirstOrDefaultAsync<(string? OID, string? ManagerOID)>(
                            "SELECT [OID], [managerOID] FROM [OrganizationUnit] WHERE [id] = @DeptCode",
                            new { DeptCode = emp.Dept1Code },
                            transaction);
                        if (string.IsNullOrEmpty(deptRow.OID))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions 略過 {emp.EmpNo}: 找不到部門 OrganizationUnit (DeptCode={emp.Dept1Code})");
                            continue;
                        }
                        string organizationUnitOID = deptRow.OID;

                        // 直屬主管：若本部門主管就是自己 (自己是部門主管)，改沿 superUnitOID
                        // 往上層部門找「不是自己」的主管，而不是把自己填成自己的直屬主管。
                        // 2026-09-09 修正：一路找到組織最頂層都找不到 (例如董事長，上面已經沒有部門)，
                        // 維持自己是自己的直屬主管，不要留空——這也是 BPM 既有資料在頂層的慣例寫法，
                        // 留空反而是異常值。
                        string? specifiedManagerOID = deptRow.ManagerOID;
                        if (!string.IsNullOrEmpty(specifiedManagerOID) &&
                            string.Equals(specifiedManagerOID, occupantOID, StringComparison.OrdinalIgnoreCase))
                        {
                            var resolvedManagerOID = await ResolveNonSelfManagerOIDAsync(
                                connection, transaction, organizationUnitOID, occupantOID);
                            specifiedManagerOID = !string.IsNullOrEmpty(resolvedManagerOID) ? resolvedManagerOID : occupantOID;
                        }

                        // 4. definitionOID：104 職稱(JobName) → FunctionDefinition（僅查詢比對，不自動新增）
                        if (string.IsNullOrWhiteSpace(emp.JobName))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions 略過 {emp.EmpNo}: 104 未回傳職稱 (JobName)");
                            continue;
                        }

                        string? definitionOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [FunctionDefinition] WHERE [organizationOID] = @CompanyOID AND [functionDefinitionName] = @JobName",
                            new { CompanyOID = companyOID, JobName = emp.JobName.Trim() },
                            transaction);
                        if (string.IsNullOrEmpty(definitionOID))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions 略過 {emp.EmpNo}: BPM FunctionDefinition 尚未建立職稱「{emp.JobName}」，請先於 BPM 後台建檔後再重新同步");
                            continue;
                        }

                        // 5. approvalLevelOID：2026-09-03 修正 — 職稱若剛好對應到主管職核決層級名稱
                        //    (例如職稱「經理」對到 FunctionLevel「經理」)，直接代入該層級；
                        //    職稱對不到任何主管層級名稱時 (一般非主管職稱)，才 fallback 用 defaultLevel。
                        //    (實測全庫資料：經理/課長/處長/副課長等主管職稱多數確實對應同名層級，
                        //    非主管職稱如工程師/專員/管理師則幾乎全部落在 defaultLevel，符合這個規則)
                        string? approvalLevelOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [FunctionLevel] WHERE [organizationOID] = @CompanyOID AND [functionLevelName] = @JobName",
                            new { CompanyOID = companyOID, JobName = emp.JobName.Trim() },
                            transaction);

                        if (string.IsNullOrEmpty(approvalLevelOID))
                        {
                            approvalLevelOID = await connection.QueryFirstOrDefaultAsync<string>(
                                "SELECT [OID] FROM [FunctionLevel] WHERE [organizationOID] = @CompanyOID AND [functionLevelName] = 'defaultLevel'",
                                new { CompanyOID = companyOID },
                                transaction);
                            if (string.IsNullOrEmpty(approvalLevelOID))
                            {
                                _logger.Warning($"[BPM] Functions {emp.EmpNo}: 找不到 FunctionLevel 'defaultLevel' (CO_CODE={emp.CompanyCode})，approvalLevelOID 將為 NULL");
                            }
                        }

                        // 6. UPSERT Functions（比對鍵：occupantOID + organizationUnitOID）
                        var existingOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Functions] WHERE [occupantOID] = @OccupantOID AND [organizationUnitOID] = @OrgUnitOID",
                            new { OccupantOID = occupantOID, OrgUnitOID = organizationUnitOID },
                            transaction);

                        if (!string.IsNullOrEmpty(existingOID))
                        {
                            await connection.ExecuteAsync(@"
                                UPDATE [Functions] SET
                                    [definitionOID]       = @DefinitionOID,
                                    [approvalLevelOID]    = @ApprovalLevelOID,
                                    [specifiedManagerOID] = @SpecifiedManagerOID,
                                    [isMain]              = 1,
                                    [objectVersion]       = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = existingOID,
                                    DefinitionOID = definitionOID,
                                    ApprovalLevelOID = (object?)approvalLevelOID ?? DBNull.Value,
                                    SpecifiedManagerOID = (object?)specifiedManagerOID ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Functions", "UPDATE", emp.EmpNo, true);
                            _logger.LogSyncRecord("Functions",
                                $"OID={existingOID}, occupantOID={occupantOID}, organizationUnitOID={organizationUnitOID}, " +
                                $"definitionOID={definitionOID}, approvalLevelOID={approvalLevelOID ?? "NULL"}, " +
                                $"specifiedManagerOID={specifiedManagerOID ?? "NULL"} (UPDATE)");
                        }
                        else
                        {
                            var newOID = await GenerateUniqueOIDAsync(connection, transaction,
                                "Functions", "Users", "Employee", "OrganizationUnit", "Organization",
                                "OrganizationUnitLevel", "FunctionDefinition", "FunctionLevel");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [Functions] (
                                    [OID], [objectVersion], [approvalLevelOID], [definitionOID],
                                    [occupantOID], [organizationUnitOID], [specifiedManagerOID], [isMain]
                                ) VALUES (
                                    @OID, 1, @ApprovalLevelOID, @DefinitionOID,
                                    @OccupantOID, @OrgUnitOID, @SpecifiedManagerOID, 1
                                )",
                                new
                                {
                                    OID = newOID,
                                    ApprovalLevelOID = (object?)approvalLevelOID ?? DBNull.Value,
                                    DefinitionOID = definitionOID,
                                    OccupantOID = occupantOID,
                                    OrgUnitOID = organizationUnitOID,
                                    SpecifiedManagerOID = (object?)specifiedManagerOID ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Functions", "INSERT", emp.EmpNo, true);
                            _logger.LogSyncRecord("Functions",
                                $"OID={newOID}, occupantOID={occupantOID}, organizationUnitOID={organizationUnitOID}, " +
                                $"definitionOID={definitionOID}, approvalLevelOID={approvalLevelOID ?? "NULL"}, " +
                                $"specifiedManagerOID={specifiedManagerOID ?? "NULL"}, objectVersion=1, isMain=1");
                        }

                        result.SuccessCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] Functions 同步進度: {processedCount}/{employees.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"員工 {emp.EmpNo} ({emp.EmpName}) Functions: {ex.Message}");
                        _logger.LogSyncDetail("Functions", "SYNC", emp.EmpNo, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"Functions ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 Functions 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }

        /// <summary>
        /// 2026-09-09 新增：同步部門「兼職主管」到 Functions 表 (isMain=0)。
        /// 情境：104 部門的 LEADER_EMP_NO 不一定等於該主管自己的 DEPT1_CODE
        /// (例如黃嘉偉本業掛在 A0440，卻同時是 A0443 的部門主管)——SyncEmployeeFunctionsAsync
        /// 只會處理主管自己 DEPT1_CODE 對應的那筆 Functions (isMain=1)，不會建立/更新兼職部門這筆，
        /// 導致兼職那筆資料一直停留在建立當下的舊值 (實測發現過 objectVersion=1、主管欄位是好幾個組織
        /// 改組前的舊人選)。
        /// 職稱(definitionOID)/核決層級(approvalLevelOID) 沿用該主管自己「本職」(isMain=1) 的 Functions
        /// 記錄，因為 104 部門資料本身沒有回傳職稱可用；查不到本職記錄 (代表這位主管還沒被同步過)
        /// 就跳過，等下次他自己被同步到後再補。
        ///
        /// 2026-09-09 補強：**跨公司**掛名主管 (例如集團董事長何瑞祥被指定管理 YCS Germany GmbH
        /// 的 I0100董事長室，但 104 從未把他登記成這家公司的員工) 除了 Functions 缺記錄外，
        /// 還會缺一筆「主管在該部門所屬公司底下的 Employee 記錄」。實測證實 BPM 展開部門時，
        /// 會對這個部門底下每個 Functions 佔用者執行
        /// `SELECT ... FROM Users, Employee WHERE Users.OID=@occupant AND Employee.userOID=Users.OID
        ///  AND Employee.organizationOID=@該部門所屬公司OID`，
        /// 查無資料(0筆)會導致 BPM 後端接口調用失敗、整個部門展開不了。
        /// 客戶已確認正式環境「分公司可以掛不隸屬該公司的人當部門主管」是合法情境，
        /// 所以主動幫他在該公司底下補一筆 Employee 記錄 (即使 104 沒有把他列為該公司員工)，
        /// 讓 BPM 查得到資料，不會再是空結果。
        /// 需在 SyncEmployeeFunctionsAsync 之後執行。
        /// </summary>
        public async Task<SyncResult> SyncConcurrentDeptHeadFunctionsAsync(List<Department> departments, long coId, string coCode)
        {
            var result = new SyncResult { DataType = "Functions(兼職主管)", TargetSystem = "BPM" };
            if (departments == null || departments.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                string? companyOID = await GetOrganizationOIDAsync(connection, transaction, coCode);
                if (string.IsNullOrEmpty(companyOID))
                {
                    _logger.Warning($"[BPM] Functions(兼職主管) 找不到 Organization (CO_CODE={coCode})，跳過");
                    transaction.Commit();
                    return result;
                }

                var deptsWithLeader = departments.Where(d => !string.IsNullOrEmpty(d.LeaderEmpNo)).ToList();
                result.TotalCount = deptsWithLeader.Count;
                int processedCount = 0;

                foreach (var dept in deptsWithLeader)
                {
                    processedCount++;
                    try
                    {
                        // 1. 部門本身的 OID
                        string? organizationUnitOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @DeptCode",
                            new { DeptCode = dept.DeptCode },
                            transaction);
                        if (string.IsNullOrEmpty(organizationUnitOID))
                        {
                            result.SkippedCount++;
                            continue;
                        }

                        // 2. 主管的 Users.OID
                        string? leaderUserOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Users] WHERE [id] = @LeaderEmpNo",
                            new { dept.LeaderEmpNo },
                            transaction);
                        if (string.IsNullOrEmpty(leaderUserOID))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions(兼職主管) 略過 部門{dept.DeptCode}主管{dept.LeaderEmpNo}: 找不到對應 Users 記錄");
                            continue;
                        }

                        // 2.5 確保主管在「這個部門所屬公司」底下有 Employee 記錄（跨公司掛名主管專用補強）
                        //     104 可能沒把他登記成這家公司的員工，但正式環境允許跨公司掛名主管，
                        //     BPM 展開部門時會查這筆 Employee，查無資料就會展開失敗，所以主動補上。
                        int hasEmployeeForThisCompany = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM [Employee] WHERE [userOID] = @UserOID AND [organizationOID] = @CompanyOID",
                            new { UserOID = leaderUserOID, CompanyOID = companyOID },
                            transaction);

                        if (hasEmployeeForThisCompany == 0)
                        {
                            var newEmpOID = await GenerateUniqueOIDAsync(connection, transaction,
                                "Employee", "Users", "OrganizationUnit", "Organization",
                                "OrganizationUnitLevel", "FunctionDefinition", "FunctionLevel", "Functions");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [Employee] (
                                    [OID], [employeeId], [organizationOID], [userOID], [objectVersion], [validTo]
                                ) VALUES (
                                    @OID, @EmployeeId, @OrganizationOID, @UserOID, 1, NULL
                                )",
                                new
                                {
                                    OID = newEmpOID,
                                    EmployeeId = dept.LeaderEmpNo,
                                    OrganizationOID = companyOID,
                                    UserOID = leaderUserOID
                                },
                                transaction);

                            _logger.LogSyncDetail("Employee(跨公司掛名主管)", "INSERT", dept.LeaderEmpNo!, true);
                            _logger.LogSyncRecord("Employee",
                                $"OID={newEmpOID} (跨公司掛名主管補建), employeeId={dept.LeaderEmpNo}, " +
                                $"organizationOID={companyOID} ({coCode}), userOID={leaderUserOID}, objectVersion=1");
                        }

                        // 3. 已經是本職 (isMain=1 且部門相同) 就不算兼職，交給主同步邏輯處理，這裡跳過避免重複
                        int alreadyMainHere = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM [Functions] WHERE [occupantOID] = @OccupantOID AND [organizationUnitOID] = @OrgUnitOID AND [isMain] = 1",
                            new { OccupantOID = leaderUserOID, OrgUnitOID = organizationUnitOID },
                            transaction);
                        if (alreadyMainHere > 0)
                        {
                            continue;
                        }

                        // 4. 沿用主管自己「本職」(isMain=1) 的職稱/核決層級；查不到代表他還沒被同步過，先跳過
                        var mainFunction = await connection.QueryFirstOrDefaultAsync<(string? DefinitionOID, string? ApprovalLevelOID)>(
                            "SELECT TOP 1 [definitionOID], [approvalLevelOID] FROM [Functions] WHERE [occupantOID] = @OccupantOID AND [isMain] = 1",
                            new { OccupantOID = leaderUserOID },
                            transaction);
                        if (string.IsNullOrEmpty(mainFunction.DefinitionOID))
                        {
                            result.SkippedCount++;
                            _logger.Warning($"[BPM] Functions(兼職主管) 略過 部門{dept.DeptCode}主管{dept.LeaderEmpNo}: 查不到主管本職的職稱，無法決定兼職記錄的 definitionOID (該主管可能尚未被同步過)");
                            continue;
                        }

                        // 5. 直屬主管：兼職部門本身的 managerOID (理論上就是他自己)；若一路往上找不到不是自己的主管，維持自己
                        var deptManagerRow = await connection.QueryFirstOrDefaultAsync<(string? ManagerOID, string? SuperUnitOID)>(
                            "SELECT [managerOID], [superUnitOID] FROM [OrganizationUnit] WHERE [OID] = @OID",
                            new { OID = organizationUnitOID },
                            transaction);
                        string? specifiedManagerOID = deptManagerRow.ManagerOID;
                        if (!string.IsNullOrEmpty(specifiedManagerOID) &&
                            string.Equals(specifiedManagerOID, leaderUserOID, StringComparison.OrdinalIgnoreCase))
                        {
                            var resolved = await ResolveNonSelfManagerOIDAsync(connection, transaction, organizationUnitOID, leaderUserOID);
                            specifiedManagerOID = !string.IsNullOrEmpty(resolved) ? resolved : leaderUserOID;
                        }

                        // 6. UPSERT（比對鍵：occupantOID + organizationUnitOID，isMain=0）
                        var existingOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Functions] WHERE [occupantOID] = @OccupantOID AND [organizationUnitOID] = @OrgUnitOID",
                            new { OccupantOID = leaderUserOID, OrgUnitOID = organizationUnitOID },
                            transaction);

                        if (!string.IsNullOrEmpty(existingOID))
                        {
                            await connection.ExecuteAsync(@"
                                UPDATE [Functions] SET
                                    [definitionOID]       = @DefinitionOID,
                                    [approvalLevelOID]    = @ApprovalLevelOID,
                                    [specifiedManagerOID] = @SpecifiedManagerOID,
                                    [isMain]              = 0,
                                    [objectVersion]       = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = existingOID,
                                    DefinitionOID = mainFunction.DefinitionOID,
                                    ApprovalLevelOID = (object?)mainFunction.ApprovalLevelOID ?? DBNull.Value,
                                    SpecifiedManagerOID = (object?)specifiedManagerOID ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Functions(兼職主管)", "UPDATE", dept.LeaderEmpNo!, true);
                            _logger.LogSyncRecord("Functions",
                                $"OID={existingOID} (兼職), occupantOID={leaderUserOID}, organizationUnitOID={organizationUnitOID} ({dept.DeptCode}), " +
                                $"definitionOID={mainFunction.DefinitionOID}, approvalLevelOID={mainFunction.ApprovalLevelOID ?? "NULL"}, " +
                                $"specifiedManagerOID={specifiedManagerOID ?? "NULL"}, isMain=0 (UPDATE)");
                        }
                        else
                        {
                            var newOID = await GenerateUniqueOIDAsync(connection, transaction,
                                "Functions", "Users", "Employee", "OrganizationUnit", "Organization",
                                "OrganizationUnitLevel", "FunctionDefinition", "FunctionLevel");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [Functions] (
                                    [OID], [objectVersion], [approvalLevelOID], [definitionOID],
                                    [occupantOID], [organizationUnitOID], [specifiedManagerOID], [isMain]
                                ) VALUES (
                                    @OID, 1, @ApprovalLevelOID, @DefinitionOID,
                                    @OccupantOID, @OrgUnitOID, @SpecifiedManagerOID, 0
                                )",
                                new
                                {
                                    OID = newOID,
                                    ApprovalLevelOID = (object?)mainFunction.ApprovalLevelOID ?? DBNull.Value,
                                    DefinitionOID = mainFunction.DefinitionOID,
                                    OccupantOID = leaderUserOID,
                                    OrgUnitOID = organizationUnitOID,
                                    SpecifiedManagerOID = (object?)specifiedManagerOID ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Functions(兼職主管)", "INSERT", dept.LeaderEmpNo!, true);
                            _logger.LogSyncRecord("Functions",
                                $"OID={newOID} (兼職新增), occupantOID={leaderUserOID}, organizationUnitOID={organizationUnitOID} ({dept.DeptCode}), " +
                                $"definitionOID={mainFunction.DefinitionOID}, approvalLevelOID={mainFunction.ApprovalLevelOID ?? "NULL"}, " +
                                $"specifiedManagerOID={specifiedManagerOID ?? "NULL"}, objectVersion=1, isMain=0");
                        }

                        result.SuccessCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] Functions(兼職主管) 同步進度: {processedCount}/{deptsWithLeader.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"部門 {dept.DeptCode} 兼職主管 {dept.LeaderEmpNo}: {ex.Message}");
                        _logger.LogSyncDetail("Functions(兼職主管)", "SYNC", dept.LeaderEmpNo ?? dept.DeptCode, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"Functions兼職主管 ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 Functions(兼職主管) 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }

        /// <summary>
        /// 2026-09-03 新增：沿著 OrganizationUnit.superUnitOID 往上層部門尋找「不是自己」的主管 OID。
        /// 用途：某員工本身就是所屬部門的主管時，其直屬主管不該是自己，改抓上一階部門的主管。
        /// 若上層部門也沒有主管，或主管仍是自己，就繼續往上找；一路到頂層都找不到則回傳 null。
        /// maxDepth 與 visited 集合是為了防呆，避免 superUnitOID 資料異常成環時無窮迴圈。
        /// </summary>
        private static async Task<string?> ResolveNonSelfManagerOIDAsync(
            IDbConnection connection, IDbTransaction transaction,
            string organizationUnitOID, string selfUserOID, int maxDepth = 20)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentOID = organizationUnitOID;

            for (int depth = 0; depth < maxDepth && !string.IsNullOrEmpty(currentOID); depth++)
            {
                if (!visited.Add(currentOID)) break;

                var row = await connection.QueryFirstOrDefaultAsync<(string? ManagerOID, string? SuperUnitOID)>(
                    "SELECT [managerOID], [superUnitOID] FROM [OrganizationUnit] WHERE [OID] = @OID",
                    new { OID = currentOID },
                    transaction);

                if (!string.IsNullOrEmpty(row.ManagerOID) &&
                    !string.Equals(row.ManagerOID, selfUserOID, StringComparison.OrdinalIgnoreCase))
                {
                    return row.ManagerOID;
                }

                currentOID = row.SuperUnitOID;
            }

            return null;
        }

        #endregion

        #region ERP 方法存根 (BPM Service 不實作，回傳空結果)

        public Task<SyncResult> SyncGemFileAsync(List<Department> departments)
            => Task.FromResult(new SyncResult { DataType = "gem_file", TargetSystem = "BPM(跳過)" });

        public Task<SyncResult> SyncAbdFileAsync(List<DeptHierarchy> hierarchy)
            => Task.FromResult(new SyncResult { DataType = "abd_file", TargetSystem = "BPM(跳過)" });

        public Task<SyncResult> SyncAbdFileFromDepartmentsAsync(List<Department> departments)
            => Task.FromResult(new SyncResult { DataType = "abd_file", TargetSystem = "BPM(跳過)" });

        public Task<SyncResult> SyncGenFileAsync(List<Employee> employees, Dictionary<string, string>? managerEmpNoMap = null)
            => Task.FromResult(new SyncResult { DataType = "gen_file", TargetSystem = "BPM(跳過)" });

        #endregion
    }
}
