using System.Data;
using Dapper;
using Oracle.ManagedDataAccess.Client;
using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// ERP (Oracle) 資料庫服務
    /// 對照表: api_erp_bpm_mapping.md
    ///
    /// 寫入的 Table:
    ///   - YCS.GEM_FILE : 部門資料 (INSERT/UPDATE)
    ///   - YCS.ABD_FILE : 部門層級關係 (INSERT/UPDATE)
    ///   - YCS.GEN_FILE : 員工資料 (INSERT/UPDATE)
    ///
    /// 不寫入的 Table (已在 TIPTOP 端建立):
    ///   - geu_file : 公司 (不需同步)
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
                        // UPSERT: 以 GEM01 = DEPT_CODE 判斷
                        var exists = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM YCS.GEM_FILE WHERE GEM01 = :GEM01",
                            new { GEM01 = dept.DeptCode },
                            transaction) > 0;

                        if (exists)
                        {
                            // ── Update ──
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.GEM_FILE SET
                                    GEM02   = :GEM02,
                                    GEM03   = :GEM03,
                                    GEMACTI = :GEMACTI,
                                    GEMMODU = :GEMMODU,
                                    GEMDATE = SYSDATE
                                    --待確認 GEM04, GEM05, GEM07, GEM09, GEM10, GEMGRUP, GEMORIG
                                WHERE GEM01 = :GEM01",
                                new
                                {
                                    GEM01 = dept.DeptCode,
                                    GEM02 = dept.DeptName ?? "",
                                    GEM03 = dept.DeptAbbr ?? dept.DeptName ?? "",
                                    GEMACTI = dept.IsAct == 1 ? "Y" : "N",
                                    GEMMODU = "SYNC104"
                                },
                                transaction);

                            _logger.LogSyncDetail("gem_file", "UPDATE", dept.DeptCode, true);
                        }
                        else
                        {
                            // ── Insert ──
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.GEM_FILE (
                                    GEM01, GEM02, GEM03,
                                    --待確認 GEM04 用途 (標準 TIPTOP 標 No Use, YCS 標上層部門)
                                    GEM04,
                                    --待確認 GEM05 是否為會計部門
                                    GEM05,
                                    GEM06, GEM07, GEM08,
                                    GEMACTI, GEMUSER,
                                    --待確認 GEMGRUP
                                    GEMGRUP,
                                    GEMMODU, GEMDATE,
                                    --待確認 GEM09 管理類別 (1=成本中心 2=利潤中心)
                                    GEM09,
                                    --待確認 GEM10 對應成本中心
                                    GEM10,
                                    GEM11,
                                    --待確認 GEMORIG 資料建立部門
                                    GEMORIG,
                                    GEMORIU
                                ) VALUES (
                                    :GEM01, :GEM02, :GEM03,
                                    --待確認 GEM04
                                    NULL,
                                    --待確認 GEM05
                                    NULL,
                                    NULL, NULL, NULL,
                                    :GEMACTI, :GEMUSER,
                                    --待確認 GEMGRUP
                                    NULL,
                                    :GEMMODU, SYSDATE,
                                    --待確認 GEM09
                                    NULL,
                                    --待確認 GEM10
                                    NULL,
                                    NULL,
                                    --待確認 GEMORIG
                                    NULL,
                                    :GEMORIU
                                )",
                                new
                                {
                                    GEM01 = dept.DeptCode,
                                    GEM02 = dept.DeptName ?? "",
                                    GEM03 = dept.DeptAbbr ?? dept.DeptName ?? "",
                                    GEMACTI = dept.IsAct == 1 ? "Y" : "N",
                                    GEMUSER = "SYNC104",
                                    GEMMODU = "SYNC104",
                                    GEMORIU = "SYNC104"
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

        #region abd_file（部門層級關係）

        /// <summary>
        /// ABD_FILE 層級邏輯 (mapping doc):
        ///   abd01 = 父層部門編號
        ///   abd02 = 子層部門編號
        ///   頂層部門: abd01 = abd02 = 自己
        ///
        /// 資料來源: 從 Department.ParentDeptCode 取得父層
        /// 此方法需要 departments 的層級關係，而非 dept_level API
        /// </summary>
        public async Task<SyncResult> SyncAbdFileFromDepartmentsAsync(List<Department> departments)
        {
            var result = new SyncResult { DataType = "abd_file", TargetSystem = "ERP" };
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
                        // abd01 = 父層 (若無父層則 = 自己)
                        // abd02 = 自己
                        var parentCode = string.IsNullOrEmpty(dept.ParentDeptCode)
                            ? dept.DeptCode
                            : dept.ParentDeptCode;

                        var abd01 = parentCode.Length > 10 ? parentCode[..10] : parentCode;
                        var abd02 = dept.DeptCode.Length > 10 ? dept.DeptCode[..10] : dept.DeptCode;

                        // UPSERT: 以 ABD01 + ABD02 複合 PK 判斷
                        var exists = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM YCS.ABD_FILE WHERE ABD01 = :ABD01 AND ABD02 = :ABD02",
                            new { ABD01 = abd01, ABD02 = abd02 },
                            transaction) > 0;

                        if (!exists)
                        {
                            // ── Insert ──
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.ABD_FILE (
                                    ABD01, ABD02,
                                    ABD03, ABD04, ABD05, ABD06,
                                    ABDACTI, ABDUSER,
                                    --待確認 ABDGRUP
                                    ABDGRUP,
                                    ABDMODU, ABDDATE,
                                    --待確認 ABDORIG
                                    ABDORIG,
                                    ABDORIU
                                ) VALUES (
                                    :ABD01, :ABD02,
                                    NULL, NULL, NULL, NULL,
                                    :ABDACTI, :ABDUSER,
                                    --待確認 ABDGRUP
                                    NULL,
                                    :ABDMODU, SYSDATE,
                                    --待確認 ABDORIG
                                    NULL,
                                    :ABDORIU
                                )",
                                new
                                {
                                    ABD01 = abd01,
                                    ABD02 = abd02,
                                    ABDACTI = "Y",
                                    ABDUSER = "SYNC104",
                                    ABDMODU = "SYNC104",
                                    ABDORIU = "SYNC104"
                                },
                                transaction);

                            _logger.LogSyncDetail("abd_file", "INSERT", $"{abd01}-{abd02}", true);
                        }
                        else
                        {
                            // ── Update（確保有效碼） ──
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.ABD_FILE SET
                                    ABDACTI = :ABDACTI,
                                    ABDMODU = :ABDMODU,
                                    ABDDATE = SYSDATE
                                WHERE ABD01 = :ABD01 AND ABD02 = :ABD02",
                                new
                                {
                                    ABD01 = abd01,
                                    ABD02 = abd02,
                                    ABDACTI = "Y",
                                    ABDMODU = "SYNC104"
                                },
                                transaction);

                            _logger.LogSyncDetail("abd_file", "UPDATE", $"{abd01}-{abd02}", true);
                        }

                        result.SuccessCount++;

                        if (processedCount % 100 == 0)
                            _logger.Info($"[{GetDatabaseName()}] 部門層級同步進度: {processedCount}/{departments.Count}");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        result.Errors.Add($"部門層級 {dept.DeptCode}: {ex.Message}");
                        _logger.LogSyncDetail("abd_file", "SYNC", dept.DeptCode, false, ex.Message);
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

        // IDatabaseService 介面的多載 (dept_level API 版本 — 不再使用, 改用 Department 版本)
        public async Task<SyncResult> SyncAbdFileAsync(List<DeptHierarchy> hierarchy)
        {
            _logger.Info("[ERP] abd_file 層級關係改從 Department.ParentDeptCode 寫入，dept_level API 版本不再使用");
            return new SyncResult { DataType = "abd_file", TargetSystem = "ERP(跳過)" };
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

                        if (exists)
                        {
                            // ── Update ──
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.GEN_FILE SET
                                    GEN02   = :GEN02,
                                    --待確認 GEN03 部門代號
                                    --待確認 GEN04 職稱
                                    --待確認 GEN05, GEN06
                                    GENACTI = :GENACTI,
                                    GENMODU = :GENMODU,
                                    GENDATE = SYSDATE
                                WHERE GEN01 = :GEN01",
                                new
                                {
                                    GEN01 = emp.EmpNo,
                                    GEN02 = emp.EmpName ?? "",
                                    GENACTI = emp.WorkStatus == 3 ? "N" : "Y",
                                    GENMODU = "SYNC104"
                                },
                                transaction);

                            _logger.LogSyncDetail("gen_file", "UPDATE", emp.EmpNo, true);
                        }
                        else
                        {
                            // ── Insert ──
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.GEN_FILE (
                                    GEN01, GEN02,
                                    --待確認 GEN03 部門代號
                                    GEN03,
                                    --待確認 GEN04 職稱
                                    GEN04,
                                    --待確認 GEN05
                                    GEN05,
                                    --待確認 GEN06
                                    GEN06,
                                    GENACTI, GENUSER,
                                    --待確認 GENGRUP
                                    GENGRUP,
                                    GENMODU, GENDATE,
                                    --待確認 TA_GEN07, TA_GEN08
                                    TA_GEN07, TA_GEN08
                                ) VALUES (
                                    :GEN01, :GEN02,
                                    --待確認 GEN03
                                    NULL,
                                    --待確認 GEN04
                                    NULL,
                                    --待確認 GEN05
                                    NULL,
                                    --待確認 GEN06
                                    NULL,
                                    :GENACTI, :GENUSER,
                                    --待確認 GENGRUP
                                    NULL,
                                    :GENMODU, SYSDATE,
                                    --待確認 TA_GEN07, TA_GEN08
                                    NULL, NULL
                                )",
                                new
                                {
                                    GEN01 = emp.EmpNo,
                                    GEN02 = emp.EmpName ?? "",
                                    GENACTI = emp.WorkStatus == 3 ? "N" : "Y",
                                    GENUSER = "SYNC104",
                                    GENMODU = "SYNC104"
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

        #region BPM 方法存根 (ERP Service 不實作，回傳空結果)

        public Task<SyncResult> SyncOrganizationAsync(List<CompanyInfo> companies)
            => Task.FromResult(new SyncResult { DataType = "Organization", TargetSystem = "ERP(跳過)" });

        public Task<SyncResult> SyncOrganizationUnitsAsync(List<Department> departments, long coId, string coCode)
            => Task.FromResult(new SyncResult { DataType = "OrganizationUnit", TargetSystem = "ERP(跳過)" });

        public Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId)
            => Task.FromResult(new SyncResult { DataType = "OrganizationUnitLevel", TargetSystem = "ERP(跳過)" });

        public Task<SyncResult> SyncEmployeesAsync(List<Employee> employees, long coId)
            => Task.FromResult(new SyncResult { DataType = "Employee", TargetSystem = "ERP(跳過)" });

        #endregion
    }
}
