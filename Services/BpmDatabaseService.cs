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
    ///   - OrganizationUnitLevel : 部門層級名稱 (INSERT/UPDATE)
    ///   - Users                 : 系統使用者 (INSERT/UPDATE)
    ///   - Employee              : 員工歸屬 (INSERT/UPDATE)
    ///   - Functions             : 職稱/簽核歸屬 (INSERT/UPDATE，2026-09-03 新增)
    ///
    /// 不寫入的 Table (已在 BPM 管理端建立，只查詢比對):
    ///   - Organization          : 公司
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

                var levelOidMap = new Dictionary<(int, string?), string>();
                foreach (var row in await connection.QueryAsync(
                    "SELECT [levelValue], [organizationOID], [OID] FROM [OrganizationUnitLevel]",
                    transaction: transaction))
                {
                    levelOidMap[((int)row.levelValue, (string?)row.organizationOID)] = (string)row.OID;
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

                        string? managerOID = null;
                        if (!string.IsNullOrEmpty(dept.LeaderEmpNo))
                            userOidMap.TryGetValue(dept.LeaderEmpNo, out managerOID);

                        string? superUnitOID = null;
                        if (!string.IsNullOrEmpty(dept.ParentDeptCode))
                            orgUnitOidMap.TryGetValue(dept.ParentDeptCode, out superUnitOID);

                        string? levelOID = null;
                        if (dept.DeptLevelId.HasValue)
                            levelOidMap.TryGetValue(((int)dept.DeptLevelId.Value, organizationOID), out levelOID);

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
                                    OrgUnitName = dept.DeptName ?? "",
                                    ManagerOID = (object?)managerOID ?? DBNull.Value,
                                    SuperUnitOID = (object?)superUnitOID ?? DBNull.Value,
                                    LevelOID = (object?)levelOID ?? DBNull.Value,
                                    ValidType = dept.IsAct == 1 ? 1 : 0
                                },
                                transaction);

                            result.SuccessCount++;
                            _logger.LogSyncDetail("OrganizationUnit", "UPDATE", dept.DeptCode, true);
                            _logger.LogSyncRecord("OrganizationUnit",
                                $"OID={existingOid}, id={dept.DeptCode}, organizationUnitName={dept.DeptName}, " +
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
                                    OrgUnitName = dept.DeptName ?? "",
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
                                $"OID={oid}, id={dept.DeptCode}, organizationUnitName={dept.DeptName}, " +
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

        public async Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId, string coCode)
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
                        // ═══════════════════════════════════
                        var empOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Employee] WHERE [employeeId] = @EmpNo",
                            new { EmpNo = emp.EmpNo },
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
        /// 2026-09-03 修正: 改以 104 Dept1Code 直接查 OrganizationUnit，不再經由 Employee.organizationOID
        /// (該欄位已改回指向 Organization 公司層級，不再是部門)。
        /// </summary>
        public async Task<Dictionary<string, string>> GetEmployeeManagerEmpNosAsync(List<Employee> employees)
        {
            var managerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var deptCodes = employees.Select(e => e.Dept1Code).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            if (deptCodes.Count == 0) return managerMap;

            using var connection = CreateConnection();
            connection.Open();

            var rows = await connection.QueryAsync<(string DeptCode, string? ManagerEmpNo)>(@"
                SELECT ou.[id] AS DeptCode, mgrUser.[id] AS ManagerEmpNo
                FROM [OrganizationUnit] ou
                LEFT JOIN [Users] mgrUser ON ou.[managerOID] = mgrUser.[OID]
                WHERE ou.[id] IN @DeptCodes",
                new { DeptCodes = deptCodes });

            var deptManagerMap = rows
                .Where(r => !string.IsNullOrEmpty(r.ManagerEmpNo))
                .ToDictionary(r => r.DeptCode, r => r.ManagerEmpNo!, StringComparer.OrdinalIgnoreCase);

            foreach (var emp in employees)
            {
                if (!string.IsNullOrEmpty(emp.Dept1Code) && deptManagerMap.TryGetValue(emp.Dept1Code, out var mgrEmpNo))
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
                        string? specifiedManagerOID = deptRow.ManagerOID;

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

                        // 5. approvalLevelOID：預設採用 defaultLevel（全庫多數情況），查無則留 NULL
                        string? approvalLevelOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [FunctionLevel] WHERE [organizationOID] = @CompanyOID AND [functionLevelName] = 'defaultLevel'",
                            new { CompanyOID = companyOID },
                            transaction);
                        if (string.IsNullOrEmpty(approvalLevelOID))
                        {
                            _logger.Warning($"[BPM] Functions {emp.EmpNo}: 找不到 FunctionLevel 'defaultLevel' (CO_CODE={emp.CompanyCode})，approvalLevelOID 將為 NULL");
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
