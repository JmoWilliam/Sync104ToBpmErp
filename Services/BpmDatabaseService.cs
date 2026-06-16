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
    ///
    /// 不寫入的 Table (已在 BPM 管理端建立):
    ///   - Organization          : 公司 (不需同步)
    /// </summary>
    public class BpmDatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private readonly ILoggerService _logger;
        private readonly int _batchSize;

        public BpmDatabaseService(DatabaseSettings settings, ILoggerService logger, int batchSize = 100)
        {
            _connectionString = settings.ConnectionString;
            _logger = logger;
            _batchSize = batchSize;
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

                result.TotalCount = departments.Count;
                int processedCount = 0;

                foreach (var dept in departments)
                {
                    processedCount++;
                    try
                    {
                        // 查詢已存在的 OID (by id = DEPT_CODE)
                        var existingOid = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @Id",
                            new { Id = dept.DeptCode },
                            transaction);

                        // ── 查詢關聯 OID ──
                        // managerOID: LEADER_EMP_NO → Users.id → Users.OID
                        string? managerOID = null;
                        if (!string.IsNullOrEmpty(dept.LeaderEmpNo))
                        {
                            managerOID = await connection.QueryFirstOrDefaultAsync<string>(
                                "SELECT [OID] FROM [Users] WHERE [id] = @EmpNo",
                                new { EmpNo = dept.LeaderEmpNo },
                                transaction);
                        }

                        // superUnitOID: PARENT_DEPT_CODE → OrganizationUnit.id → OrganizationUnit.OID
                        string? superUnitOID = null;
                        if (!string.IsNullOrEmpty(dept.ParentDeptCode))
                        {
                            superUnitOID = await connection.QueryFirstOrDefaultAsync<string>(
                                "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @ParentId",
                                new { ParentId = dept.ParentDeptCode },
                                transaction);
                        }

                        // levelOID: DEPT_LEVEL_ID → OrganizationUnitLevel.OID
                        // OrganizationUnitLevel 用 levelValue 存 DeptLevelId，查它
                        string? levelOID = null;
                        if (dept.DeptLevelId.HasValue)
                        {
                            levelOID = await connection.QueryFirstOrDefaultAsync<string>(
                                "SELECT [OID] FROM [OrganizationUnitLevel] WHERE [levelValue] = @LevelValue AND [organizationOID] = @OrgOID",
                                new { LevelValue = (int)dept.DeptLevelId.Value, OrgOID = organizationOID ?? (object)DBNull.Value },
                                transaction);
                        }

                        if (!string.IsNullOrEmpty(existingOid))
                        {
                            // ── Update ──
                            await connection.ExecuteAsync(@"
                                UPDATE [OrganizationUnit] SET
                                    [organizationUnitName] = @OrgUnitName,
                                    [managerOID]           = @ManagerOID,
                                    [superUnitOID]         = @SuperUnitOID,
                                    [levelOID]             = @LevelOID,
                                    [organizationOID]      = @OrganizationOID,
                                    [validType]            = @ValidType,
                                    --待確認 organizationUnitType 對應104什麼值
                                    [organizationUnitType] = 1,
                                    [objectVersion]        = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = existingOid,
                                    OrgUnitName = dept.DeptName ?? "",
                                    ManagerOID = (object?)managerOID ?? DBNull.Value,
                                    SuperUnitOID = (object?)superUnitOID ?? DBNull.Value,
                                    LevelOID = (object?)levelOID ?? DBNull.Value,
                                    OrganizationOID = (object?)organizationOID ?? DBNull.Value,
                                    ValidType = dept.IsAct == 1 ? 1 : 0
                                },
                                transaction);

                            _logger.LogSyncDetail("OrganizationUnit", "UPDATE", dept.DeptCode, true);
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

                            _logger.LogSyncDetail("OrganizationUnit", "INSERT", dept.DeptCode, true);
                        }

                        result.SuccessCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 部門同步進度: {processedCount}/{departments.Count}");
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

        public async Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId)
        {
            var result = new SyncResult { DataType = "OrganizationUnitLevel", TargetSystem = "BPM" };
            if (hierarchy == null || hierarchy.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                // OrganizationUnitLevel 透過 organizationOID 對應到公司
                // 但 dept_level API 回傳的 CO_ID 要對應到 BPM Organization.id = CompanyCode
                // 先透過 CO_ID 反查 104 company → CO_CODE → Organization.id
                // 然而 dept_level API 沒有直接回傳 CO_CODE，這裡需要從外部傳入
                // 解決方案: 用 CO_ID 去查公司對照表，或用傳入的 coId 去對應

                // ── 待確認: coId 對應到 Organization.id (CO_CODE) 的方式 ──
                // 目前需要由呼叫端傳入 companyCode
                // 暫且先用 coId.ToString() 當作 CO_CODE 查 Organization
                string? organizationOID = await GetOrganizationOIDAsync(connection, transaction, coId.ToString());
                if (string.IsNullOrEmpty(organizationOID))
                {
                    _logger.Warning($"[BPM] 找不到 Organization (by CO_ID={coId})，層級將不帶 organizationOID");
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
                            // ── Update ──
                            await connection.ExecuteAsync(@"
                                UPDATE [OrganizationUnitLevel] SET
                                    [organizationUnitLevelName] = @LevelName,
                                    [objectVersion] = [objectVersion] + 1
                                    --待確認 description
                                WHERE [OID] = @OID",
                                new { OID = existingOid, LevelName = item.LevelName },
                                transaction);

                            _logger.LogSyncDetail("OrganizationUnitLevel", "UPDATE", item.LevelName, true);
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
                        }

                        result.SuccessCount++;

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

                        if (!string.IsNullOrEmpty(userOID))
                        {
                            // ── Update Users ──
                            await connection.ExecuteAsync(@"
                                UPDATE [Users] SET
                                    [userName]          = @UserName,
                                    [mailAddress]       = @MailAddress,
                                    [phoneNumber]       = @Phone,
                                    [leaveDate]         = @LeaveDate,
                                    [objectVersion]     = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = userOID,
                                    UserName = emp.EmpName,
                                    MailAddress = (object?)emp.Email ?? DBNull.Value,
                                    Phone = (object?)emp.Phone ?? DBNull.Value,
                                    LeaveDate = (object?)emp.QuitDate ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Users", "UPDATE", emp.EmpNo, true);
                        }
                        else
                        {
                            // ── Insert Users ──
                            userOID = await GenerateUniqueOIDAsync(connection, transaction,
                                "Users", "Employee", "OrganizationUnit", "Organization", "OrganizationUnitLevel");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [Users] (
                                    [OID], [id], [userName], [objectVersion], [password],
                                    [leaveDate], [mailAddress], [localeString], [phoneNumber],
                                    --待確認 identificationType
                                    [identificationType],
                                    [enableSubstitute], [mailingFrequencyType],
                                    [performForwardType], [userTaskDisplay], [createdTime]
                                ) VALUES (
                                    @OID, @Id, @UserName, 1, @Password,
                                    @LeaveDate, @MailAddress, 'zh_TW', @Phone,
                                    --待確認 identificationType
                                    'Employee',
                                    0, 0,
                                    0, 1, SYSDATETIME()
                                )",
                                new
                                {
                                    OID = userOID,
                                    Id = emp.EmpNo,
                                    UserName = emp.EmpName,
                                    //--待確認 password 規則
                                    Password = "0000",
                                    LeaveDate = (object?)emp.QuitDate ?? DBNull.Value,
                                    MailAddress = (object?)emp.Email ?? DBNull.Value,
                                    Phone = (object?)emp.Phone ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Users", "INSERT", emp.EmpNo, true);
                        }

                        // ═══════════════════════════════════
                        // 2. 查詢 organizationOID（部門）
                        //    DEPT1_CODE → OrganizationUnit.id → OrganizationUnit.OID
                        // ═══════════════════════════════════
                        string? organizationOID = null;
                        if (!string.IsNullOrEmpty(emp.Dept1Code))
                        {
                            organizationOID = await connection.QueryFirstOrDefaultAsync<string>(
                                "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @DeptCode",
                                new { DeptCode = emp.Dept1Code },
                                transaction);
                        }

                        // ═══════════════════════════════════
                        // 3. Employee（員工歸屬）— UPSERT
                        // ═══════════════════════════════════
                        var empOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Employee] WHERE [employeeId] = @EmpNo",
                            new { EmpNo = emp.EmpNo },
                            transaction);

                        if (!string.IsNullOrEmpty(empOID))
                        {
                            // ── Update Employee ──
                            await connection.ExecuteAsync(@"
                                UPDATE [Employee] SET
                                    [organizationOID] = @OrganizationOID,
                                    [userOID]          = @UserOID,
                                    [objectVersion]    = [objectVersion] + 1,
                                    [validTo]          = @ValidTo
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = empOID,
                                    OrganizationOID = (object?)organizationOID ?? DBNull.Value,
                                    UserOID = userOID,
                                    ValidTo = (object?)emp.QuitDate ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Employee", "UPDATE", emp.EmpNo, true);
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

                            _logger.LogSyncDetail("Employee", "INSERT", emp.EmpNo, true);
                        }

                        result.SuccessCount++;

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

        #region ERP 方法存根 (BPM Service 不實作，回傳空結果)

        public Task<SyncResult> SyncGemFileAsync(List<Department> departments)
            => Task.FromResult(new SyncResult { DataType = "gem_file", TargetSystem = "BPM(跳過)" });

        public Task<SyncResult> SyncAbdFileAsync(List<DeptHierarchy> hierarchy)
            => Task.FromResult(new SyncResult { DataType = "abd_file", TargetSystem = "BPM(跳過)" });

        public Task<SyncResult> SyncGenFileAsync(List<Employee> employees)
            => Task.FromResult(new SyncResult { DataType = "gen_file", TargetSystem = "BPM(跳過)" });

        #endregion
    }
}
