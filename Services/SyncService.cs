using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// 同步服務 - 協調 HR API 與各目標系統的資料同步
    /// 對照表: api_erp_bpm_mapping.md
    ///
    /// 同步項目:
    ///   BPM: OrganizationUnit, Users, Employee, Functions
    ///   ERP: gem_file, abd_file, gen_file
    /// 不同步: Organization, geu_file, OrganizationUnitLevel, FunctionDefinition, FunctionLevel
    ///        (已在管理端建立；OrganizationUnitLevel 2026-09-07 起改為僅查詢比對，不再寫入)
    /// </summary>
    public class SyncService
    {
        private readonly IHRApiService _hrApiService;
        private readonly IDatabaseService _bpmDatabaseService;
        private readonly IDatabaseService _erpDatabaseService;
        private readonly ILoggerService _logger;

        public SyncService(
            IHRApiService hrApiService,
            IDatabaseService bpmDatabaseService,
            IDatabaseService erpDatabaseService,
            ILoggerService logger)
        {
            _hrApiService = hrApiService;
            _bpmDatabaseService = bpmDatabaseService;
            _erpDatabaseService = erpDatabaseService;
            _logger = logger;
        }

        /// <summary>
        /// 執行完整同步流程（支援多公司）
        /// </summary>
        public async Task<SyncReport> RunFullSyncAsync(DateTime startTime, DateTime endTime)
        {
            var report = new SyncReport
            {
                StartTime = DateTime.Now,
                SyncDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                QueryStartTime = startTime,
                QueryEndTime = endTime
            };

            try
            {
                _logger.Info("============================================");
                _logger.Info("[排程開始] HR 資料同步排程");
                _logger.Info($"執行時間: {report.StartTime:yyyy-MM-dd HH:mm:ss}");
                _logger.Info($"查詢時間範圍: {startTime:yyyy-MM-dd HH:mm:ss} ~ {endTime:yyyy-MM-dd HH:mm:ss}");
                _logger.Info("============================================");

                // 測試資料庫連線
                await TestConnectionsAsync();

                // ═══════════════════════════════════════════════
                // Step 1: 取得公司清單（不寫入 Organization/geu_file）
                //         → 公司只在 104 端管理，僅查詢比對
                // ═══════════════════════════════════════════════
                var companies = await _hrApiService.GetCompaniesAsync();
                if (companies.Count == 0)
                {
                    _logger.Warning("[HR API] 回傳的公司資料為空，無法繼續同步");
                    report.Success = true;
                    report.EndTime = DateTime.Now;
                    report.Duration = report.EndTime - report.StartTime;
                    return report;
                }

                _logger.Info($"[公司清單] 取得 {companies.Count} 筆公司資料（僅查詢比對，不寫入 BPM/ERP）");
                foreach (var c in companies)
                {
                    _logger.Info($"  CO_ID={c.CompanyId}, CO_CODE={c.CompanyCode}, CO_NAME={c.CompanyName}");
                }

                // ═══════════════════════════════════════════════
                // Step 2: 依公司 Loop 執行同步
                // 每家公司獨立 try/catch：單一公司失敗（例如 104 未授權查詢該公司）
                // 只記錄錯誤並跳過，不影響其他公司繼續同步
                // ═══════════════════════════════════════════════
                report.Success = true;

                foreach (var company in companies)
                {
                    var coId = company.CompanyId;
                    var coCode = company.CompanyCode;

                    _logger.Info("============================================");
                    _logger.Info($"[公司處理] 開始處理公司 CO_ID={coId}, CO_CODE={coCode}");
                    _logger.Info("============================================");

                    try
                    {
                        // ─── 2.1 部門層級名稱 (dept_level API → BPM OrganizationUnitLevel) ───
                        await SyncDeptHierarchyAsync(coId, coCode, report);

                        // ─── 2.2 部門資料 (dept API → BPM OrganizationUnit + ERP gem_file + ERP abd_file) ───
                        var departments = await SyncDepartmentsAsync(startTime, endTime, coId, coCode, report);

                        // ─── 2.3 員工資料 (emp API → BPM Users+Employee + ERP gen_file) ───
                        await SyncEmployeesAsync(startTime, endTime, coId, report);

                        // ─── 2.4 部門兼職主管 (dept.LEADER_EMP_NO 本業不在該部門時，補一筆 isMain=0 的 Functions) ───
                        //     需排在 2.3 之後：兼職主管的核決層級/職稱是沿用他自己「本職」的 Functions 記錄，
                        //     本職記錄要先存在才查得到。
                        await SyncConcurrentDeptHeadsAsync(departments, coId, coCode, report);
                    }
                    catch (HrApiPermissionDeniedException ex)
                    {
                        // 沒有權限：直接顯示清楚訊息，不印出完整例外堆疊，也不再嘗試此公司後續的同步步驟
                        report.Success = false;
                        report.ErrorMessage = string.IsNullOrEmpty(report.ErrorMessage)
                            ? $"CO_ID={coId} (CO_CODE={coCode}): 沒有權限"
                            : $"{report.ErrorMessage} | CO_ID={coId} (CO_CODE={coCode}): 沒有權限";

                        _logger.Warning($"[公司處理] CO_ID={coId}, CO_CODE={coCode} 沒有權限，跳過此公司 — {ex.Message}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        report.Success = false;
                        report.ErrorMessage = string.IsNullOrEmpty(report.ErrorMessage)
                            ? $"CO_ID={coId} (CO_CODE={coCode}): {ex.Message}"
                            : $"{report.ErrorMessage} | CO_ID={coId} (CO_CODE={coCode}): {ex.Message}";

                        _logger.Error($"[公司處理失敗] CO_ID={coId}, CO_CODE={coCode} 同步時發生錯誤，已跳過此公司並繼續處理下一家", ex);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                report.Success = false;
                report.ErrorMessage = ex.Message;
                _logger.Error("[排程失敗] 同步排程執行失敗", ex);
            }
            finally
            {
                report.EndTime = DateTime.Now;
                report.Duration = report.EndTime - report.StartTime;

                _logger.Info("============================================");
                _logger.Info("[排程結束] HR 資料同步排程");
                _logger.Info($"執行結果: {(report.Success ? "成功" : "失敗")}");
                _logger.Info($"耗時: {report.Duration.TotalSeconds:F2} 秒");
                _logger.Info("============================================");
            }

            return report;
        }

        /// <summary>
        /// 測試所有資料庫連線
        /// </summary>
        private async Task TestConnectionsAsync()
        {
            _logger.Info("[連線測試] 正在測試資料庫連線...");

            var bpmConnected = await _bpmDatabaseService.TestConnectionAsync();
            var erpConnected = await _erpDatabaseService.TestConnectionAsync();

            if (!bpmConnected)
            {
                throw new Exception("無法連線到 BPM 資料庫 (MS-SQL)，請檢查連線設定");
            }

            if (!erpConnected)
            {
                throw new Exception("無法連線到 ERP 資料庫 (Oracle)，請檢查連線設定");
            }

            _logger.Info("[連線測試] 所有資料庫連線測試通過");
        }

        /// <summary>
        /// 2.1 同步部門資料
        ///     寫入: BPM OrganizationUnit + ERP gem_file + ERP abd_file
        ///     回傳這次抓到的部門清單，供之後 2.4 兼職主管同步重複使用，不用再多打一次 API
        /// </summary>
        private async Task<List<Department>> SyncDepartmentsAsync(DateTime startTime, DateTime endTime, long coId, string coCode, SyncReport report)
        {
            _logger.LogSyncStart($"Department (CO_ID={coId})", startTime, endTime);

            try
            {
                // 從 HR API 取得部門資料
                var departments = await _hrApiService.GetDepartmentsAsync(startTime, endTime, coId);

                if (departments.Count == 0)
                {
                    _logger.Warning($"[HR API] 無部門資料 (CO_ID={coId})，跳過");
                    return departments;
                }

                _logger.Info($"[同步處理] 取得 {departments.Count} 筆部門資料 (CO_ID={coId})");

                // 同步到 BPM OrganizationUnit
                _logger.Info("[同步處理] 正在同步到 BPM (OrganizationUnit)...");
                var bpmResult = await _bpmDatabaseService.SyncOrganizationUnitsAsync(departments, coId, coCode);
                report.SetBpmDepartmentResult(coId, bpmResult);

                // 同步到 ERP gem_file
                _logger.Info("[同步處理] 正在同步到 ERP (gem_file)...");
                var erpResult = await _erpDatabaseService.SyncGemFileAsync(departments);
                report.SetErpDepartmentResult(coId, erpResult);

                // 同步到 ERP abd_file（部門層級關係，用 Department.ParentDeptCode）
                _logger.Info("[同步處理] 正在同步部門層級關係到 ERP (abd_file)...");
                var abdResult = await _erpDatabaseService.SyncAbdFileFromDepartmentsAsync(departments);
                report.SetErpHierarchyResult(coId, abdResult);

                _logger.Info($"[同步完成] 部門資料同步完成 (CO_ID={coId}) - " +
                    $"BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, " +
                    $"ERP gem: {erpResult.SuccessCount}/{erpResult.TotalCount}, " +
                    $"ERP abd: {abdResult.SuccessCount}/{abdResult.TotalCount}");

                return departments;
            }
            catch (HrApiPermissionDeniedException)
            {
                // 交由外層 (RunFullSyncAsync 公司迴圈) 統一印出「沒有權限」訊息並跳過，這裡不重複記錄完整堆疊
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[同步錯誤] 同步部門資料時發生錯誤 (CO_ID={coId})", ex);
                throw;
            }
        }

        /// <summary>
        /// 2.2 同步部門層級名稱（dept_level API → BPM OrganizationUnitLevel）
        ///     注意: 部門層級關係不在這裡寫，改由 abd_file 從 Department.ParentDeptCode 寫入
        /// </summary>
        private async Task SyncDeptHierarchyAsync(long coId, string coCode, SyncReport report)
        {
            _logger.Info($"[同步開始] DeptHierarchy 層級名稱 (CO_ID={coId})");

            try
            {
                var hierarchy = await _hrApiService.GetDeptHierarchyAsync(coId);

                if (hierarchy.Count == 0)
                {
                    _logger.Warning($"[HR API] 無部門層級名稱資料 (CO_ID={coId})，跳過");
                    return;
                }

                _logger.Info($"[同步處理] 取得 {hierarchy.Count} 筆部門層級名稱 (CO_ID={coId})");

                // 同步到 BPM OrganizationUnitLevel
                _logger.Info("[同步處理] 正在同步到 BPM (OrganizationUnitLevel)...");
                var bpmResult = await _bpmDatabaseService.SyncOrganizationUnitLevelsAsync(hierarchy, coId, coCode);
                report.SetBpmHierarchyResult(coId, bpmResult);

                _logger.Info($"[同步完成] 部門層級名稱同步完成 (CO_ID={coId}) - " +
                    $"BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}");
            }
            catch (HrApiPermissionDeniedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[同步錯誤] 同步部門層級名稱時發生錯誤 (CO_ID={coId})", ex);
                throw;
            }
        }

        /// <summary>
        /// 2.3 同步員工資料
        ///     寫入: BPM Users+Employee + ERP gen_file
        /// </summary>
        private async Task SyncEmployeesAsync(DateTime startTime, DateTime endTime, long coId, SyncReport report)
        {
            _logger.LogSyncStart($"Employee (CO_ID={coId})", startTime, endTime);

            try
            {
                var employees = await _hrApiService.GetEmployeesAsync(startTime, endTime, coId);

                if (employees.Count == 0)
                {
                    _logger.Warning($"[HR API] 無員工資料 (CO_ID={coId})，跳過");
                    return;
                }

                _logger.Info($"[同步處理] 取得 {employees.Count} 筆員工資料 (CO_ID={coId})");

                // 同步到 BPM (Users + Employee)
                _logger.Info("[同步處理] 正在同步到 BPM (Users + Employee)...");
                var bpmResult = await _bpmDatabaseService.SyncEmployeesAsync(employees, coId);
                report.SetBpmEmployeeResult(coId, bpmResult);

                // 查詢直屬主管工號 (BPM OrganizationUnit.managerOID)，
                // 需在 BPM Employee 資料已同步完成後才查得到 organizationOID，故安排在此處執行
                _logger.Info("[同步處理] 正在查詢員工直屬主管工號 (BPM)...");
                var managerEmpNoMap = await _bpmDatabaseService.GetEmployeeManagerEmpNosAsync(employees);

                // 同步職稱/簽核歸屬到 BPM Functions（需在 Users+Employee 同步完成後執行，才能查到 occupantOID）
                _logger.Info("[同步處理] 正在同步到 BPM (Functions)...");
                var functionsResult = await _bpmDatabaseService.SyncEmployeeFunctionsAsync(employees, coId);
                report.SetBpmFunctionsResult(coId, functionsResult);

                // 同步到 ERP gen_file
                _logger.Info("[同步處理] 正在同步到 ERP (gen_file)...");
                var erpResult = await _erpDatabaseService.SyncGenFileAsync(employees, managerEmpNoMap);
                report.SetErpEmployeeResult(coId, erpResult);

                _logger.Info($"[同步完成] 員工資料同步完成 (CO_ID={coId}) - " +
                    $"BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, " +
                    $"Functions: {functionsResult.SuccessCount}/{functionsResult.TotalCount} (跳過 {functionsResult.SkippedCount}), " +
                    $"ERP: {erpResult.SuccessCount}/{erpResult.TotalCount}");
            }
            catch (HrApiPermissionDeniedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[同步錯誤] 同步員工資料時發生錯誤 (CO_ID={coId})", ex);
                throw;
            }
        }

        /// <summary>
        /// 2.4 同步部門「兼職主管」到 BPM Functions（isMain=0）
        /// 2026-09-09 新增：104 部門資料的 LEADER_EMP_NO 不一定等於該主管自己的 DEPT1_CODE
        /// (例如黃嘉偉本業掛在 A0440，卻是 A0443 的部門主管)，這種情況屬於兼職，
        /// 2.3 的主同步只會處理主管自己 DEPT1_CODE 對應的那筆 Functions (isMain=1)，
        /// 不會建立/更新兼職部門這筆，導致兼職那筆資料一直停留在建立當下的舊值。
        /// 直接用 2.2 已經抓到的部門清單，逐一檢查 LEADER_EMP_NO 是否為兼職，是的話補上/更新
        /// isMain=0 的 Functions 記錄。
        /// </summary>
        private async Task SyncConcurrentDeptHeadsAsync(List<Department> departments, long coId, string coCode, SyncReport report)
        {
            if (departments == null || departments.Count == 0) return;

            try
            {
                _logger.Info($"[同步處理] 正在同步部門兼職主管到 BPM (Functions, isMain=0)... (CO_ID={coId})");
                var result = await _bpmDatabaseService.SyncConcurrentDeptHeadFunctionsAsync(departments, coId, coCode);
                report.SetBpmConcurrentHeadResult(coId, result);

                _logger.Info($"[同步完成] 部門兼職主管同步完成 (CO_ID={coId}) - " +
                    $"BPM: {result.SuccessCount}/{result.TotalCount} (跳過 {result.SkippedCount})");
            }
            catch (HrApiPermissionDeniedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[同步錯誤] 同步部門兼職主管時發生錯誤 (CO_ID={coId})", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// 同步報告（支援多公司）
    /// </summary>
    public class SyncReport
    {
        public string SyncDate { get; set; } = string.Empty;
        public DateTime QueryStartTime { get; set; }
        public DateTime QueryEndTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        // BPM 各表結果（以公司 CO_ID 為 key）
        public Dictionary<long, SyncResult> BpmDepartmentResults { get; set; } = new();
        public Dictionary<long, SyncResult> BpmHierarchyResults { get; set; } = new();
        public Dictionary<long, SyncResult> BpmEmployeeResults { get; set; } = new();
        public Dictionary<long, SyncResult> BpmFunctionsResults { get; set; } = new();
        public Dictionary<long, SyncResult> BpmConcurrentHeadResults { get; set; } = new();

        // ERP 各表結果（以公司 CO_ID 為 key）
        public Dictionary<long, SyncResult> ErpDepartmentResults { get; set; } = new();
        public Dictionary<long, SyncResult> ErpHierarchyResults { get; set; } = new();
        public Dictionary<long, SyncResult> ErpEmployeeResults { get; set; } = new();

        public void SetBpmDepartmentResult(long coId, SyncResult r) => BpmDepartmentResults[coId] = r;
        public void SetBpmHierarchyResult(long coId, SyncResult r) => BpmHierarchyResults[coId] = r;
        public void SetBpmEmployeeResult(long coId, SyncResult r) => BpmEmployeeResults[coId] = r;
        public void SetBpmFunctionsResult(long coId, SyncResult r) => BpmFunctionsResults[coId] = r;
        public void SetBpmConcurrentHeadResult(long coId, SyncResult r) => BpmConcurrentHeadResults[coId] = r;
        public void SetErpDepartmentResult(long coId, SyncResult r) => ErpDepartmentResults[coId] = r;
        public void SetErpHierarchyResult(long coId, SyncResult r) => ErpHierarchyResults[coId] = r;
        public void SetErpEmployeeResult(long coId, SyncResult r) => ErpEmployeeResults[coId] = r;

        /// <summary>
        /// 產生文字報告
        /// </summary>
        public string GenerateTextReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("HR 資料同步報告");
            sb.AppendLine("========================================");
            sb.AppendLine($"同步日期: {SyncDate}");
            sb.AppendLine($"查詢時間範圍: {QueryStartTime:yyyy-MM-dd HH:mm:ss} ~ {QueryEndTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"執行結果: {(Success ? "成功" : "失敗")}");
            sb.AppendLine($"耗時: {Duration.TotalSeconds:F2} 秒");
            if (!Success && !string.IsNullOrEmpty(ErrorMessage))
            {
                sb.AppendLine($"錯誤訊息: {ErrorMessage}");
            }
            sb.AppendLine();

            // BPM 結果
            sb.AppendLine("--- BPM (電子簽核) 同步結果 ---");
            foreach (var kv in BpmDepartmentResults)
                sb.AppendLine($"  部門 OrganizationUnit(CO_ID={kv.Key}): 新增 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過(已存在) {kv.Value.SkippedCount}");
            foreach (var kv in BpmHierarchyResults)
                sb.AppendLine($"  層級名 OrganizationUnitLevel(CO_ID={kv.Key}): 新增 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過(已存在) {kv.Value.SkippedCount}");
            foreach (var kv in BpmEmployeeResults)
                sb.AppendLine($"  員工 Employee+Users(CO_ID={kv.Key}): 新增 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過(已存在) {kv.Value.SkippedCount}");
            foreach (var kv in BpmFunctionsResults)
                sb.AppendLine($"  職稱/簽核 Functions(CO_ID={kv.Key}): 成功 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過(缺職稱/部門對應) {kv.Value.SkippedCount}");
            foreach (var kv in BpmConcurrentHeadResults)
                sb.AppendLine($"  兼職主管 Functions(CO_ID={kv.Key}): 成功 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過 {kv.Value.SkippedCount}");
            sb.AppendLine();

            // ERP 結果
            sb.AppendLine("--- ERP (TIPTOP) 同步結果 ---");
            foreach (var kv in ErpDepartmentResults)
                sb.AppendLine($"  部門 gem_file(CO_ID={kv.Key}): 新增 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過(已存在) {kv.Value.SkippedCount}");
            foreach (var kv in ErpHierarchyResults)
                sb.AppendLine($"  層級 abd_file(CO_ID={kv.Key}): 新增 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過(已存在) {kv.Value.SkippedCount}");
            foreach (var kv in ErpEmployeeResults)
                sb.AppendLine($"  員工 gen_file(CO_ID={kv.Key}): 新增 {kv.Value.SuccessCount}/{kv.Value.TotalCount}, 跳過(已存在) {kv.Value.SkippedCount}");
            sb.AppendLine();

            // 錯誤詳情
            var allErrors = new List<string>();
            foreach (var kv in BpmDepartmentResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in BpmHierarchyResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in BpmEmployeeResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in BpmFunctionsResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in BpmConcurrentHeadResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in ErpDepartmentResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in ErpHierarchyResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in ErpEmployeeResults) allErrors.AddRange(kv.Value.Errors);

            if (allErrors.Count > 0)
            {
                sb.AppendLine("--- 錯誤詳情 ---");
                foreach (var error in allErrors.Take(20))
                {
                    sb.AppendLine($"- {error}");
                }
                if (allErrors.Count > 20)
                {
                    sb.AppendLine($"... 還有 {allErrors.Count - 20} 筆錯誤 (請查看 ErrorLog 檔案)");
                }
            }

            sb.AppendLine("========================================");

            return sb.ToString();
        }
    }
}
