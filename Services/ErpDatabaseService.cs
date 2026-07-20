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
                            // ── 資料已存在 — UPDATE (2026-07-16 依客戶回覆 DataMappingUserConfirm20260716.xlsx 啟用)
                            //    僅更新客戶確認需要異動的欄位: GEM02, GEM03, GEMACTI, GEM09 (固定寫 3)
                            //    GEM04/05/06/07/08/GEMGRUP/GEM10 客戶未勾選需要更新，維持原值不動 ──
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.GEM_FILE SET
                                    GEM02   = :GEM02,
                                    GEM03   = :GEM03,
                                    GEMACTI = :GEMACTI,
                                    GEM09   = :GEM09,
                                    GEMMODU = :GEMMODU,
                                    GEMDATE = SYSDATE
                                WHERE GEM01 = :GEM01",
                                new
                                {
                                    GEM01 = dept.DeptCode,
                                    GEM02 = dept.DeptName ?? "",
                                    GEM03 = dept.DeptAbbr ?? dept.DeptName ?? "",
                                    GEMACTI = dept.IsAct == 1 ? "Y" : "N",
                                    GEM09 = "3",
                                    GEMMODU = "SYNC104"
                                },
                                transaction);

                            result.SuccessCount++;
                            _logger.LogSyncDetail("gem_file", "UPDATE", dept.DeptCode, true);
                            _logger.LogSyncRecord("gem_file",
                                $"GEM01={dept.DeptCode}, GEM02={dept.DeptName}, GEM03={dept.DeptAbbr ?? dept.DeptName}, " +
                                $"GEMACTI={(dept.IsAct == 1 ? "Y" : "N")}, GEM09=3, GEMMODU=SYNC104 (UPDATE)");
                        }
                        else
                        {
                            // ── Insert ──
                            // 注意: 客戶實際的 YCS.GEM_FILE 沒有 GEM11/GEMORIG/GEMORIU 欄位
                            // (原 mapping doc 對這三欄的假設是錯的，比照 ABD_FILE 的 ORA-00904 一併修正)
                            // GEM09 管理類別: 2026-07-16 依客戶回覆固定寫 '3' (其它)，因目前無資料來源可判斷 1=成本中心/2=利潤中心
                            // GEM10 之後直接是客製欄位 TA_GEM001~007，目前無對應資料來源，故不寫入
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
                                    GEM09,
                                    --待確認 GEM10 對應成本中心
                                    GEM10
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
                                    :GEM09,
                                    --待確認 GEM10
                                    NULL
                                )",
                                new
                                {
                                    GEM01 = dept.DeptCode,
                                    GEM02 = dept.DeptName ?? "",
                                    GEM03 = dept.DeptAbbr ?? dept.DeptName ?? "",
                                    GEMACTI = dept.IsAct == 1 ? "Y" : "N",
                                    GEMUSER = "SYNC104",
                                    GEMMODU = "SYNC104",
                                    GEM09 = "3"
                                },
                                transaction);

                            _logger.LogSyncDetail("gem_file", "INSERT", dept.DeptCode, true);
                            _logger.LogSyncRecord("gem_file",
                                $"GEM01={dept.DeptCode}, GEM02={dept.DeptName}, GEM03={dept.DeptAbbr ?? dept.DeptName}, " +
                                $"GEMACTI={(dept.IsAct == 1 ? "Y" : "N")}, GEM09=3, GEMUSER=SYNC104, GEMMODU=SYNC104");
                            result.SuccessCount++;
                        }

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
                            // 注意: 客戶實際的 YCS.ABD_FILE 只有 ABD01/02/03/04/05/06、ABDACTI、
                            // ABDUSER、ABDGRUP、ABDMODU、ABDDATE 這些欄位，沒有 ABDORIG/ABDORIU
                            // (原 mapping doc 對這兩欄的假設是錯的，實測 ORA-00904 已證實)
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.ABD_FILE (
                                    ABD01, ABD02,
                                    ABD03, ABD04, ABD05, ABD06,
                                    ABDACTI, ABDUSER,
                                    --待確認 ABDGRUP
                                    ABDGRUP,
                                    ABDMODU, ABDDATE
                                ) VALUES (
                                    :ABD01, :ABD02,
                                    NULL, NULL, NULL, NULL,
                                    :ABDACTI, :ABDUSER,
                                    --待確認 ABDGRUP
                                    NULL,
                                    :ABDMODU, SYSDATE
                                )",
                                new
                                {
                                    ABD01 = abd01,
                                    ABD02 = abd02,
                                    ABDACTI = "Y",
                                    ABDUSER = "SYNC104",
                                    ABDMODU = "SYNC104"
                                },
                                transaction);

                            _logger.LogSyncDetail("abd_file", "INSERT", $"{abd01}-{abd02}", true);
                            _logger.LogSyncRecord("abd_file", $"ABD01={abd01}, ABD02={abd02}, ABDACTI=Y, ABDUSER=SYNC104");
                            result.SuccessCount++;
                        }
                        else
                        {
                            // ── 資料已存在 — UPDATE (2026-07-16 依客戶回覆啟用，確保有效碼與異動時間同步更新) ──
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

                            result.SuccessCount++;
                            _logger.LogSyncDetail("abd_file", "UPDATE", $"{abd01}-{abd02}", true);
                            _logger.LogSyncRecord("abd_file", $"ABD01={abd01}, ABD02={abd02}, ABDACTI=Y, ABDMODU=SYNC104 (UPDATE)");
                        }

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

        public async Task<SyncResult> SyncGenFileAsync(List<Employee> employees, Dictionary<string, string>? managerEmpNoMap = null)
        {
            var result = new SyncResult { DataType = "gen_file", TargetSystem = "ERP" };
            if (employees == null || employees.Count == 0) return result;

            managerEmpNoMap ??= new Dictionary<string, string>();

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
                        // 2026-07-16 依客戶回覆 (DataMappingUserConfirm20260716.xlsx):
                        //   GEN03 = 部門代號 (DEPT1_CODE)
                        //   GEN04 = 職稱，暫不實作 (2026-07-18 客戶回覆先不做 Functions 職務比對)，維持 NULL
                        //   GEN05 = 分機 (OFFICE_TEL_EXT)，沒有就空白
                        //   GEN06 = Email (OFFICE_EMAIL)，客戶要求「一定要有」，缺漏時記錄 Warning
                        //   TA_GEN07 = 直屬主管工號 (依部門 OrganizationUnit.managerOID 查詢，見 GetEmployeeManagerEmpNosAsync)
                        //   TA_GEN08 = 員工銀行帳號，客戶要求「必須要有」，但 104 員工 API 目前的資料模型 (Models/Employee.cs)
                        //              完全沒有銀行帳號欄位，需先跟客戶/104 確認資料來源，此處暫不寫入，並記錄 Warning 提醒
                        var managerEmpNo = managerEmpNoMap.TryGetValue(emp.EmpNo, out var mgr) ? mgr : null;

                        if (string.IsNullOrWhiteSpace(emp.Email))
                            _logger.Warning($"[ERP] 員工 {emp.EmpNo} ({emp.EmpName}) 無 Email (OFFICE_EMAIL)，GEN06 將為空值，客戶要求此欄位必須要有");

                        _logger.Warning($"[ERP] 員工 {emp.EmpNo} ({emp.EmpName}) TA_GEN08 銀行帳號目前無資料來源，尚未寫入，待客戶/104 確認欄位對應");

                        // UPSERT: 以 GEN01 = EMP_NO 判斷
                        var exists = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM YCS.GEN_FILE WHERE GEN01 = :GEN01",
                            new { GEN01 = emp.EmpNo },
                            transaction) > 0;

                        if (exists)
                        {
                            // ── 資料已存在 — UPDATE (2026-07-16 依客戶回覆啟用)
                            //    僅更新客戶確認需要異動的欄位: GEN02, GEN03, GEN06, GENACTI
                            //    GEN05 一併更新以保持與 INSERT 邏輯一致；GEN04(職稱)暫不實作；TA_GEN07/TA_GEN08 客戶回覆未列入異動範圍，維持原值不動 ──
                            await connection.ExecuteAsync(@"
                                UPDATE YCS.GEN_FILE SET
                                    GEN02   = :GEN02,
                                    GEN03   = :GEN03,
                                    GEN05   = :GEN05,
                                    GEN06   = :GEN06,
                                    GENACTI = :GENACTI,
                                    GENMODU = :GENMODU,
                                    GENDATE = SYSDATE
                                WHERE GEN01 = :GEN01",
                                new
                                {
                                    GEN01 = emp.EmpNo,
                                    GEN02 = emp.EmpName ?? "",
                                    GEN03 = (object?)emp.Dept1Code ?? DBNull.Value,
                                    GEN05 = (object?)emp.PhoneExt ?? DBNull.Value,
                                    GEN06 = (object?)emp.Email ?? DBNull.Value,
                                    GENACTI = emp.WorkStatus == 3 ? "N" : "Y",
                                    GENMODU = "SYNC104"
                                },
                                transaction);

                            result.SuccessCount++;
                            _logger.LogSyncDetail("gen_file", "UPDATE", emp.EmpNo, true);
                            _logger.LogSyncRecord("gen_file",
                                $"GEN01={emp.EmpNo}, GEN02={emp.EmpName}, GEN03={emp.Dept1Code ?? "NULL"}, " +
                                $"GEN05={emp.PhoneExt ?? "NULL"}, GEN06={emp.Email ?? "NULL"}, " +
                                $"GENACTI={(emp.WorkStatus == 3 ? "N" : "Y")}, GENMODU=SYNC104 (UPDATE)");
                        }
                        else
                        {
                            // ── Insert ──
                            await connection.ExecuteAsync(@"
                                INSERT INTO YCS.GEN_FILE (
                                    GEN01, GEN02,
                                    GEN03,
                                    --待確認 GEN04 職稱 (暫不實作)
                                    GEN04,
                                    GEN05, GEN06,
                                    GENACTI, GENUSER,
                                    --待確認 GENGRUP
                                    GENGRUP,
                                    GENMODU, GENDATE,
                                    TA_GEN07,
                                    --待確認 TA_GEN08 (銀行帳號，尚無資料來源)
                                    TA_GEN08
                                ) VALUES (
                                    :GEN01, :GEN02,
                                    :GEN03,
                                    --待確認 GEN04
                                    NULL,
                                    :GEN05, :GEN06,
                                    :GENACTI, :GENUSER,
                                    --待確認 GENGRUP
                                    NULL,
                                    :GENMODU, SYSDATE,
                                    :TA_GEN07,
                                    --待確認 TA_GEN08
                                    NULL
                                )",
                                new
                                {
                                    GEN01 = emp.EmpNo,
                                    GEN02 = emp.EmpName ?? "",
                                    GEN03 = (object?)emp.Dept1Code ?? DBNull.Value,
                                    GEN05 = (object?)emp.PhoneExt ?? DBNull.Value,
                                    GEN06 = (object?)emp.Email ?? DBNull.Value,
                                    GENACTI = emp.WorkStatus == 3 ? "N" : "Y",
                                    GENUSER = "SYNC104",
                                    GENMODU = "SYNC104",
                                    TA_GEN07 = (object?)managerEmpNo ?? DBNull.Value
                                },
                                transaction);

                            _logger.LogSyncDetail("gen_file", "INSERT", emp.EmpNo, true);
                            _logger.LogSyncRecord("gen_file",
                                $"GEN01={emp.EmpNo}, GEN02={emp.EmpName}, GEN03={emp.Dept1Code ?? "NULL"}, " +
                                $"GEN05={emp.PhoneExt ?? "NULL"}, GEN06={emp.Email ?? "NULL"}, " +
                                $"GENACTI={(emp.WorkStatus == 3 ? "N" : "Y")}, GENUSER=SYNC104, GENMODU=SYNC104, TA_GEN07={managerEmpNo ?? "NULL"}");
                            result.SuccessCount++;
                        }

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

        public Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId, string coCode)
            => Task.FromResult(new SyncResult { DataType = "OrganizationUnitLevel", TargetSystem = "ERP(跳過)" });

        public Task<SyncResult> SyncEmployeesAsync(List<Employee> employees, long coId)
            => Task.FromResult(new SyncResult { DataType = "Employee", TargetSystem = "ERP(跳過)" });

        public Task<Dictionary<string, string>> GetEmployeeManagerEmpNosAsync(List<Employee> employees)
            => Task.FromResult(new Dictionary<string, string>());

        #endregion
    }
}
