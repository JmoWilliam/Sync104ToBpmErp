using System.Data;
using Dapper;
using Oracle.ManagedDataAccess.Client;
using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// ERP (Oracle) 資料庫服務
    /// 主要 Table:
    ///   - gem_file : 部門資料
    ///   - abd_file : 部門層級資料
    ///   - gen_file : 員工資料
    /// </summary>
    public class ErpDatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private readonly ILoggerService _logger;
        private readonly int _batchSize;

        public ErpDatabaseService(DatabaseSettings settings, ILoggerService logger, int batchSize = 100)
        {
            _connectionString = settings.ConnectionString;
            _logger = logger;
            _batchSize = batchSize;
        }

        public string GetDatabaseName() => "ERP (Oracle)";

        private IDbConnection CreateConnection() => new OracleConnection(_connectionString);

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

        #region gem_file（部門）

        public async Task<SyncResult> SyncGemFileAsync(List<Department> departments)
        {
            var result = new SyncResult { DataType = "gem_file", TargetSystem = "ERP" };
            if (departments == null || departments.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                result.TotalCount = departments.Count;
                int processedCount = 0;

                foreach (var dept in departments)
                {
                    processedCount++;
                    try
                    {
                        // UPSERT: 以 GEM01 = DEPT_CODE 判斷存在/不存在
                        var exists = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM YCS.GEM_FILE WHERE GEM01 = :GEM01",
                            new { GEM01 = dept.DeptCode },
                            transaction) > 0;

                        if (exists)
                        {
                            // Update
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.GEM_FILE SET
                                    GEM02 = :GEM02,
                                    GEM03 = :GEM03,
                                    GEMACTI = :GEMACTI,
                                    GEMMODU = 'tiptop',
                                    GEMDATE = SYSDATE
                                WHERE GEM01 = :GEM01",
                                new
                                {
                                    GEM01 = dept.DeptCode,
                                    GEM02 = dept.DeptName ?? "",
                                    GEM03 = dept.DeptName ?? "",
                                    GEMACTI = dept.IsAct == 1 ? "Y" : "N"
                                },
                                transaction);

                            _logger.LogSyncDetail("gem_file", "UPDATE", dept.DeptCode, true);
                        }
                        else
                        {
                            // Insert
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.GEM_FILE (
                                    GEM01, GEM02, GEM03, GEM04, GEM05, GEM06, GEM07, GEM08,
                                    GEMACTI, GEMUSER, GEMGRUP, GEMMODU, GEMDATE, GEM09, GEM10, GEM11,
                                    GEMORIG, GEMORIU
                                ) VALUES (
                                    :GEM01, :GEM02, :GEM03, NULL, 'N', NULL, NULL, NULL,
                                    :GEMACTI, 'tiptop', 'tiptop', 'tiptop', SYSDATE, '1', NULL, NULL,
                                    'tiptop', 'tiptop'
                                )",
                                new
                                {
                                    GEM01 = dept.DeptCode,
                                    GEM02 = dept.DeptName ?? "",
                                    GEM03 = dept.DeptName ?? "",
                                    GEMACTI = dept.IsAct == 1 ? "Y" : "N"
                                },
                                transaction);

                            _logger.LogSyncDetail("gem_file", "INSERT", dept.DeptCode, true);
                        }

                        result.SuccessCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 部門同步進度: {processedCount}/{departments.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"部門 {dept.DeptCode}: {ex.Message}");
                        _logger.LogSyncDetail("gem_file", "SYNC", dept.DeptCode, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"gem_file ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 gem_file 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }

        #endregion

        #region abd_file（部門層級）

        public async Task<SyncResult> SyncAbdFileAsync(List<DeptHierarchy> hierarchy)
        {
            var result = new SyncResult { DataType = "abd_file", TargetSystem = "ERP" };
            if (hierarchy == null || hierarchy.Count == 0) return result;

            using var connection = CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                result.TotalCount = hierarchy.Count;
                int processedCount = 0;

                foreach (var item in hierarchy)
                {
                    processedCount++;
                    try
                    {
                        var abd01 = ((int)(item.SortOrder ?? 0)).ToString();
                        var levelName = item.LevelName ?? "";
                        var abd02 = levelName.Length > 10 ? levelName[..10] : levelName;

                        // UPSERT: 以 ABD01 + ABD02 複合 PK 判斷
                        var exists = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM YCS.ABD_FILE WHERE ABD01 = :ABD01 AND ABD02 = :ABD02",
                            new { ABD01 = abd01, ABD02 = abd02 },
                            transaction) > 0;

                        if (exists)
                        {
                            // Update
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.ABD_FILE SET
                                    ABDACTI = :ABDACTI,
                                    ABDMODU = 'tiptop',
                                    ABDDATE = SYSDATE
                                WHERE ABD01 = :ABD01 AND ABD02 = :ABD02",
                                new
                                {
                                    ABD01 = abd01,
                                    ABD02 = abd02,
                                    ABDACTI = item.IsAct == 1 ? "Y" : "N"
                                },
                                transaction);

                            _logger.LogSyncDetail("abd_file", "UPDATE", $"{abd01}-{abd02}", true);
                        }
                        else
                        {
                            // Insert
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.ABD_FILE (
                                    ABD01, ABD02, ABD03, ABD04, ABD05, ABD06,
                                    ABDACTI, ABDUSER, ABDGRUP, ABDMODU, ABDDATE,
                                    ABDORIG, ABDORIU
                                ) VALUES (
                                    :ABD01, :ABD02, NULL, NULL, NULL, NULL,
                                    :ABDACTI, 'tiptop', 'tiptop', 'tiptop', SYSDATE,
                                    'tiptop', 'tiptop'
                                )",
                                new
                                {
                                    ABD01 = abd01,
                                    ABD02 = abd02,
                                    ABDACTI = item.IsAct == 1 ? "Y" : "N"
                                },
                                transaction);

                            _logger.LogSyncDetail("abd_file", "INSERT", $"{abd01}-{abd02}", true);
                        }

                        result.SuccessCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 層級同步進度: {processedCount}/{hierarchy.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"層級 {item.LevelName}: {ex.Message}");
                        _logger.LogSyncDetail("abd_file", "SYNC", item.LevelName ?? "(null)", false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"abd_file ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 abd_file 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }

        #endregion

        #region gen_file（員工）

        public async Task<SyncResult> SyncGenFileAsync(List<Employee> employees)
        {
            var result = new SyncResult { DataType = "gen_file", TargetSystem = "ERP" };
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
                        // UPSERT: 以 GEN01 = EMP_NO 判斷
                        var exists = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM YCS.GEN_FILE WHERE GEN01 = :GEN01",
                            new { GEN01 = emp.EmpNo },
                            transaction) > 0;

                        // GENDATE = 到職日 JOIN_DATE（若有）或 SYSDATE
                        var genDate = emp.JoinDate ?? DateTime.Now;

                        if (exists)
                        {
                            // Update
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.GEN_FILE SET
                                    GEN02 = :GEN02,
                                    GEN03 = :GEN03,
                                    GEN04 = :GEN04,
                                    GEN06 = :GEN06,
                                    GENMODU = 'tiptop',
                                    GENDATE = :GENDATE
                                WHERE GEN01 = :GEN01",
                                new
                                {
                                    GEN01 = emp.EmpNo,
                                    GEN02 = emp.EmpName ?? "",
                                    GEN03 = emp.DeptCode ?? "",
                                    GEN04 = emp.Position ?? "",
                                    GEN06 = emp.Email ?? "",
                                    GENDATE = genDate
                                },
                                transaction);

                            _logger.LogSyncDetail("gen_file", "UPDATE", emp.EmpNo, true);
                        }
                        else
                        {
                            // Insert
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.GEN_FILE (
                                    GEN01, GEN02, GEN03, GEN04, GEN05, GEN06,
                                    GENACTI, GENUSER, GENGRUP, GENMODU, GENDATE,
                                    TA_GEN07, TA_GEN08
                                ) VALUES (
                                    :GEN01, :GEN02, :GEN03, :GEN04, NULL, :GEN06,
                                    'N', 'tiptop', 'tiptop', 'tiptop', :GENDATE,
                                    NULL, NULL
                                )",
                                new
                                {
                                    GEN01 = emp.EmpNo,
                                    GEN02 = emp.EmpName ?? "",
                                    GEN03 = emp.DeptCode ?? "",
                                    GEN04 = emp.Position ?? "",
                                    GEN06 = emp.Email ?? "",
                                    GENDATE = genDate
                                },
                                transaction);

                            _logger.LogSyncDetail("gen_file", "INSERT", emp.EmpNo, true);
                        }

                        result.SuccessCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 員工同步進度: {processedCount}/{employees.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"員工 {emp.EmpNo}: {ex.Message}");
                        _logger.LogSyncDetail("gen_file", "SYNC", emp.EmpNo, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;
                _logger.LogSyncEnd($"gen_file ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步 gen_file 資料時發生錯誤，已回滾", ex);
                throw;
            }

            return result;
        }

        #endregion

        #region 保留的舊介面 + BPM 方法存根（此 Service 實際僅處理 ERP 表）

        public Task<SyncResult> SyncEmployeesAsync(List<Employee> employees)
            => throw new NotSupportedException("請改用 SyncGenFileAsync(List<Employee>)");

        public Task<SyncResult> SyncDepartmentsAsync(List<Department> departments)
            => throw new NotSupportedException("請改用 SyncGemFileAsync(List<Department>)");

        public Task<SyncResult> SyncDeptHierarchyAsync(List<DeptHierarchy> hierarchy)
            => throw new NotSupportedException("請改用 SyncAbdFileAsync(List<DeptHierarchy>)");

        // BPM 方法：ERP Service 不實作，回傳空結果
        public Task<SyncResult> SyncOrganizationAsync(List<CompanyInfo> companies)
            => Task.FromResult(new SyncResult { DataType = "Organization", TargetSystem = "ERP(跳過)" });

        public Task<SyncResult> SyncOrganizationUnitsAsync(List<Department> departments, long coId, string coCode)
            => Task.FromResult(new SyncResult { DataType = "OrganizationUnit", TargetSystem = "ERP(跳過)" });

        public Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId)
            => Task.FromResult(new SyncResult { DataType = "OrganizationUnitLevel", TargetSystem = "ERP(跳過)" });

        public Task<SyncResult> SyncEmployeesAsync(List<Employee> employees, long coId)
            => throw new NotSupportedException("請改用 SyncGenFileAsync(List<Employee>)");

        #endregion
    }
}
