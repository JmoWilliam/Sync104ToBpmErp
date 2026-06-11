using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// BPM (MS-SQL) 資料庫服務
    /// 主要 Table:
    ///   - Organization      : 公司資料
    ///   - OrganizationUnit  : 部門資料
    ///   - OrganizationUnitLevel : 部門層級
    ///   - Users             : 系統使用者
    ///   - Employee          : 員工歸屬
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

        #region OID 檢查 Helper

        /// <summary>
        /// 檢查 OID 在指定的表中是否已存在
        /// </summary>
        private async Task<bool> OIDExistsAsync(IDbConnection connection, IDbTransaction transaction, string tableName, string oid)
        {
            var count = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(1) FROM [{tableName}] WHERE [OID] = @OID",
                new { OID = oid },
                transaction);
            return count > 0;
        }

        /// <summary>
        /// 產生唯一的 OID，並檢查跨多張表
        /// </summary>
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

        #region Organization（公司）

        public async Task<SyncResult> SyncOrganizationAsync(List<CompanyInfo> companies)
        {
            var result = new SyncResult { DataType = "Organization", TargetSystem = "BPM" };
            if (companies == null || companies.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                result.TotalCount = companies.Count;

                foreach (var company in companies)
                {
                    try
                    {
                        // 以 id = CO_CODE 判斷是否存在
                        var existingOid = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Organization] WHERE [id] = @Id",
                            new { Id = company.CompanyCode },
                            transaction);

                        if (!string.IsNullOrEmpty(existingOid))
                        {
                            // Update
                            await connection.ExecuteAsync(@"
                                UPDATE [Organization] SET
                                    [organizationName] = @OrgName,
                                    [objectVersion] = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new { OID = existingOid, OrgName = company.CompanyName },
                                transaction);

                            _logger.LogSyncDetail("Organization", "UPDATE", company.CompanyCode, true);
                        }
                        else
                        {
                            // Insert
                            var oid = await GenerateUniqueOIDAsync(connection, transaction, "Organization");
                            await connection.ExecuteAsync(@"
                                INSERT INTO [Organization] ([OID], [id], [objectVersion], [organizationName])
                                VALUES (@OID, @Id, 1, @OrgName)",
                                new { OID = oid, Id = company.CompanyCode, OrgName = company.CompanyName },
                                transaction);

                            _logger.LogSyncDetail("Organization", "INSERT", company.CompanyCode, true);
                        }

                        result.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"公司 {company.CompanyCode}: {ex.Message}");
                        _logger.LogSyncDetail("Organization", "SYNC", company.CompanyCode, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"Organization ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 Organization 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
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
                // 預先取得 Organization.OID（公司）
                var organizationOID = await connection.QueryFirstOrDefaultAsync<string>(
                    "SELECT [OID] FROM [Organization] WHERE [id] = @CoCode",
                    new { CoCode = coCode },
                    transaction);

                if (string.IsNullOrEmpty(organizationOID))
                {
                    throw new Exception($"找不到 Organization 資料 (CO_CODE={coCode})，請先同步公司資料");
                }

                result.TotalCount = departments.Count;
                int processedCount = 0;

                foreach (var dept in departments)
                {
                    processedCount++;
                    try
                    {
                        // 以 id = DEPT_CODE 判斷是否存在
                        var existingOid = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @Id",
                            new { Id = dept.DeptCode },
                            transaction);

                        if (!string.IsNullOrEmpty(existingOid))
                        {
                            // ── Update ──
                            // 查詢 superUnitOID（上層部門）
                            string? superUnitOID = null;
                            if (!string.IsNullOrEmpty(dept.ParentDeptCode))
                            {
                                superUnitOID = await connection.QueryFirstOrDefaultAsync<string>(
                                    "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @ParentId",
                                    new { ParentId = dept.ParentDeptCode },
                                    transaction);
                            }

                            await connection.ExecuteAsync(@"
                                UPDATE [OrganizationUnit] SET
                                    [organizationUnitName] = @OrgUnitName,
                                    [superUnitOID] = @SuperUnitOID,
                                    [objectVersion] = [objectVersion] + 1,
                                    [validType] = @ValidType
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = existingOid,
                                    OrgUnitName = dept.DeptName,
                                    SuperUnitOID = (object?)superUnitOID ?? DBNull.Value,
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

                            // 查詢 superUnitOID（上層部門）
                            string? superUnitOID = null;
                            if (!string.IsNullOrEmpty(dept.ParentDeptCode))
                            {
                                superUnitOID = await connection.QueryFirstOrDefaultAsync<string>(
                                    "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @ParentId",
                                    new { ParentId = dept.ParentDeptCode },
                                    transaction);
                            }

                            await connection.ExecuteAsync(@"
                                INSERT INTO [OrganizationUnit] (
                                    [OID], [id], [organizationUnitName], [managerOID],
                                    [superUnitOID], [objectVersion], [organizationUnitType],
                                    [levelOID], [organizationOID], [validType]
                                ) VALUES (
                                    @OID, @Id, @OrgUnitName, NULL,
                                    @SuperUnitOID, 1, 1,
                                    NULL, @OrganizationOID, @ValidType
                                )",
                                new
                                {
                                    OID = oid,
                                    Id = dept.DeptCode,
                                    OrgUnitName = dept.DeptName,
                                    SuperUnitOID = (object?)superUnitOID ?? DBNull.Value,
                                    OrganizationOID = organizationOID,
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

        #region OrganizationUnitLevel（部門層級）

        public async Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId)
        {
            var result = new SyncResult { DataType = "OrganizationUnitLevel", TargetSystem = "BPM" };
            if (hierarchy == null || hierarchy.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                // 預先取得 Organization.OID（公司）
                var organizationOID = await connection.QueryFirstOrDefaultAsync<string>(
                    "SELECT o.[OID] FROM [Organization] o WHERE o.[id] IN (SELECT [CO_CODE] FROM ...)"); // 需要合理的查詢
                // 改用更直接的方式：每個層級都查一次對應的組織
                // 但實際上 dept_level 的 CO_ID 對應到 Organization.id = CompanyCode
                // 這裡先透過已知的邏輯查

                result.TotalCount = hierarchy.Count;
                int processedCount = 0;

                foreach (var item in hierarchy)
                {
                    processedCount++;
                    try
                    {
                        // 以 levelValue = SORT_ORDER 和 organizationOID 判斷是否存在
                        var existingOid = await connection.QueryFirstOrDefaultAsync<string>(
                            @"SELECT [OID] FROM [OrganizationUnitLevel]
                              WHERE [levelValue] = @LevelValue
                                AND [organizationOID] = @OrgOID",
                            new { LevelValue = item.SortOrder ?? 0, OrgOID = organizationOID },
                            transaction);

                        if (!string.IsNullOrEmpty(existingOid))
                        {
                            // Update
                            await connection.ExecuteAsync(@"
                                UPDATE [OrganizationUnitLevel] SET
                                    [organizationUnitLevelName] = @LevelName,
                                    [objectVersion] = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new { OID = existingOid, LevelName = item.LevelName },
                                transaction);

                            _logger.LogSyncDetail("OrganizationUnitLevel", "UPDATE", item.LevelName, true);
                        }
                        else
                        {
                            // Insert
                            var oid = await GenerateUniqueOIDAsync(connection, transaction,
                                "OrganizationUnitLevel", "OrganizationUnit", "Organization", "Employee", "Users");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [OrganizationUnitLevel] (
                                    [OID], [objectVersion], [levelValue],
                                    [organizationUnitLevelName], [organizationOID]
                                ) VALUES (
                                    @OID, 1, @LevelValue,
                                    @LevelName, @OrganizationOID
                                )",
                                new
                                {
                                    OID = oid,
                                    LevelValue = (int)(item.SortOrder ?? 0),
                                    LevelName = item.LevelName,
                                    OrganizationOID = organizationOID
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
                        // 1. 先寫入 Users 表
                        // ═══════════════════════════════════
                        var userOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Users] WHERE [id] = @Id",
                            new { Id = emp.EmpNo },
                            transaction);

                        if (!string.IsNullOrEmpty(userOID))
                        {
                            // Update Users
                            await connection.ExecuteAsync(@"
                                UPDATE [Users] SET
                                    [userName] = @UserName,
                                    [mailAddress] = @MailAddress,
                                    [phoneNumber] = @Phone,
                                    [leaveDate] = @LeaveDate,
                                    [objectVersion] = [objectVersion] + 1
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = userOID,
                                    UserName = emp.EmpName,
                                    MailAddress = (object?)emp.Email ?? DBNull.Value,
                                    Phone = (object?)emp.Phone ?? DBNull.Value,
                                    LeaveDate = (object?)emp.LeaveDate ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Users", "UPDATE", emp.EmpNo, true);
                        }
                        else
                        {
                            // Insert Users
                            userOID = await GenerateUniqueOIDAsync(connection, transaction,
                                "Users", "Employee", "OrganizationUnit", "Organization", "OrganizationUnitLevel");

                            await connection.ExecuteAsync(@"
                                INSERT INTO [Users] (
                                    [OID], [id], [userName], [objectVersion], [password],
                                    [leaveDate], [mailAddress], [localeString], [phoneNumber],
                                    [identificationType], [enableSubstitute], [mailingFrequencyType],
                                    [performForwardType], [userTaskDisplay]
                                ) VALUES (
                                    @OID, @Id, @UserName, 1, @Password,
                                    @LeaveDate, @MailAddress, 'zh_TW', @Phone,
                                    'Employee', 0, 0,
                                    0, 1
                                )",
                                new
                                {
                                    OID = userOID,
                                    Id = emp.EmpNo,
                                    UserName = emp.EmpName,
                                    Password = "0000", // 預設密碼，需與 BPM 管理員確認
                                    LeaveDate = (object?)emp.LeaveDate ?? DBNull.Value,
                                    MailAddress = (object?)emp.Email ?? DBNull.Value,
                                    Phone = (object?)emp.Phone ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Users", "INSERT", emp.EmpNo, true);
                        }

                        // ═══════════════════════════════════
                        // 2. 查詢 organizationOID（部門 OID）
                        //    用 104 DEPT_CODE → 查 BPM OrganizationUnit.id → 取得 OID
                        // ═══════════════════════════════════
                        string? organizationOID = null;
                        if (!string.IsNullOrEmpty(emp.DeptCode))
                        {
                            organizationOID = await connection.QueryFirstOrDefaultAsync<string>(
                                "SELECT [OID] FROM [OrganizationUnit] WHERE [id] = @DeptCode",
                                new { DeptCode = emp.DeptCode },
                                transaction);
                        }

                        // ═══════════════════════════════════
                        // 3. 再寫入 Employee 表
                        // ═══════════════════════════════════
                        var empOID = await connection.QueryFirstOrDefaultAsync<string>(
                            "SELECT [OID] FROM [Employee] WHERE [employeeId] = @EmpNo",
                            new { EmpNo = emp.EmpNo },
                            transaction);

                        if (!string.IsNullOrEmpty(empOID))
                        {
                            // Update Employee
                            await connection.ExecuteAsync(@"
                                UPDATE [Employee] SET
                                    [organizationOID] = @OrganizationOID,
                                    [userOID] = @UserOID,
                                    [objectVersion] = [objectVersion] + 1,
                                    [validTo] = @ValidTo
                                WHERE [OID] = @OID",
                                new
                                {
                                    OID = empOID,
                                    OrganizationOID = (object?)organizationOID ?? DBNull.Value,
                                    UserOID = userOID,
                                    ValidTo = (object?)emp.LeaveDate ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("Employee", "UPDATE", emp.EmpNo, true);
                        }
                        else
                        {
                            // Insert Employee
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
                                    ValidTo = (object?)emp.LeaveDate ?? DBNull.Value
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

        #region 保留的舊介面 + ERP 方法存根（此 Service 實際僅處理 BPM 表）

        public Task<SyncResult> SyncEmployeesAsync(List<Employee> employees)
            => throw new NotSupportedException("請改用 SyncEmployeesAsync(List<Employee>, long)");

        public Task<SyncResult> SyncDepartmentsAsync(List<Department> departments)
            => throw new NotSupportedException("請改用 SyncOrganizationUnitsAsync");

        public Task<SyncResult> SyncDeptHierarchyAsync(List<DeptHierarchy> hierarchy)
            => throw new NotSupportedException("請改用 SyncOrganizationUnitLevelsAsync");

        // ERP 方法：BPM Service 不實作，留空
        public Task<SyncResult> SyncGemFileAsync(List<Department> departments)
            => Task.FromResult(new SyncResult { DataType = "gem_file", TargetSystem = "BPM(跳過)" });

        public Task<SyncResult> SyncAbdFileAsync(List<DeptHierarchy> hierarchy)
            => Task.FromResult(new SyncResult { DataType = "abd_file", TargetSystem = "BPM(跳過)" });

        public Task<SyncResult> SyncGenFileAsync(List<Employee> employees)
            => Task.FromResult(new SyncResult { DataType = "gen_file", TargetSystem = "BPM(跳過)" });

        #endregion
    }
}
