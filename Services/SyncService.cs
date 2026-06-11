using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// 同步服務 - 協調 HR API 與各目標系統的資料同步
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
                // Step 1: 取得公司清單（取代 appsettings CompanyId）
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

                _logger.Info($"[公司清單] 取得 {companies.Count} 筆公司資料");
                foreach (var c in companies)
                {
                    _logger.Info($"  CO_ID={c.CompanyId}, CO_CODE={c.CompanyCode}, CO_NAME={c.CompanyName}");
                }

                // Step 1.5: 先將公司資料寫入 BPM Organization 表
                var orgResult = await _bpmDatabaseService.SyncOrganizationAsync(companies);
                report.BpmOrganizationResult = orgResult;

                // ═══════════════════════════════════════════════
                // Step 2: 依公司 Loop 執行同步
                // ═══════════════════════════════════════════════
                foreach (var company in companies)
                {
                    var coId = company.CompanyId;
                    var coCode = company.CompanyCode;

                    _logger.Info("============================================");
                    _logger.Info($"[公司處理] 開始處理公司 CO_ID={coId}, CO_CODE={coCode}");
                    _logger.Info("============================================");

                    // ─── 2.2 部門層級資料（先同步，供部門參考） ───
                    await SyncDeptHierarchyAsync(coId, report);

                    // ─── 2.1 部門資料 ───
                    await SyncDepartmentsAsync(startTime, endTime, coId, coCode, report);

                    // ─── 2.3 員工資料 ───
                    await SyncEmployeesAsync(startTime, endTime, coId, report);
                }

                report.Success = true;
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
        /// 2.1 同步部門資料（寫入 BPM OrganizationUnit + ERP gem_file）
        /// </summary>
        private async Task SyncDepartmentsAsync(DateTime startTime, DateTime endTime, long coId, string coCode, SyncReport report)
        {
            _logger.LogSyncStart($"Department (CO_ID={coId})", startTime, endTime);

            try
            {
                // 從 HR API 取得部門資料
                var departments = await _hrApiService.GetDepartmentsAsync(startTime, endTime, coId);

                if (departments.Count == 0)
                {
                    _logger.Warning($"[HR API] 無部門資料 (CO_ID={coId})，跳過");
                    return;
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

                _logger.Info($"[同步完成] 部門資料同步完成 (CO_ID={coId}) - " +
                    $"BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, " +
                    $"ERP: {erpResult.SuccessCount}/{erpResult.TotalCount}");
            }
            catch (Exception ex)
            {
                _logger.Error($"[同步錯誤] 同步部門資料時發生錯誤 (CO_ID={coId})", ex);
                throw;
            }
        }

        /// <summary>
        /// 2.2 同步部門層級資料（寫入 BPM OrganizationUnitLevel + ERP abd_file）
        /// </summary>
        private async Task SyncDeptHierarchyAsync(long coId, SyncReport report)
        {
            _logger.Info($"[同步開始] DeptHierarchy 資料 (CO_ID={coId})");

            try
            {
                // 從 HR API 取得部門層級資料
                var hierarchy = await _hrApiService.GetDeptHierarchyAsync(coId);

                if (hierarchy.Count == 0)
                {
                    _logger.Warning($"[HR API] 無部門層級資料 (CO_ID={coId})，跳過");
                    return;
                }

                _logger.Info($"[同步處理] 取得 {hierarchy.Count} 筆部門層級資料 (CO_ID={coId})");

                // 同步到 BPM OrganizationUnitLevel
                _logger.Info("[同步處理] 正在同步到 BPM (OrganizationUnitLevel)...");
                var bpmResult = await _bpmDatabaseService.SyncOrganizationUnitLevelsAsync(hierarchy, coId);
                report.SetBpmHierarchyResult(coId, bpmResult);

                // 同步到 ERP abd_file
                _logger.Info("[同步處理] 正在同步到 ERP (abd_file)...");
                var erpResult = await _erpDatabaseService.SyncAbdFileAsync(hierarchy);
                report.SetErpHierarchyResult(coId, erpResult);

                _logger.Info($"[同步完成] 部門層級同步完成 (CO_ID={coId}) - " +
                    $"BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, " +
                    $"ERP: {erpResult.SuccessCount}/{erpResult.TotalCount}");
            }
            catch (Exception ex)
            {
                _logger.Error($"[同步錯誤] 同步部門層級資料時發生錯誤 (CO_ID={coId})", ex);
                throw;
            }
        }

        /// <summary>
        /// 2.3 同步員工資料（寫入 BPM Users + Employee + ERP gen_file）
        /// </summary>
        private async Task SyncEmployeesAsync(DateTime startTime, DateTime endTime, long coId, SyncReport report)
        {
            _logger.LogSyncStart($"Employee (CO_ID={coId})", startTime, endTime);

            try
            {
                // 從 HR API 取得員工資料
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

                // 同步到 ERP gen_file
                _logger.Info("[同步處理] 正在同步到 ERP (gen_file)...");
                var erpResult = await _erpDatabaseService.SyncGenFileAsync(employees);
                report.SetErpEmployeeResult(coId, erpResult);

                _logger.Info($"[同步完成] 員工資料同步完成 (CO_ID={coId}) - " +
                    $"BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, " +
                    $"ERP: {erpResult.SuccessCount}/{erpResult.TotalCount}");
            }
            catch (Exception ex)
            {
                _logger.Error($"[同步錯誤] 同步員工資料時發生錯誤 (CO_ID={coId})", ex);
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

        // BPM 各表結果（以公司為 key）
        public SyncResult? BpmOrganizationResult { get; set; }
        public Dictionary<long, SyncResult> BpmDepartmentResults { get; set; } = new();
        public Dictionary<long, SyncResult> BpmHierarchyResults { get; set; } = new();
        public Dictionary<long, SyncResult> BpmEmployeeResults { get; set; } = new();

        // ERP 各表結果（以公司為 key）
        public Dictionary<long, SyncResult> ErpDepartmentResults { get; set; } = new();
        public Dictionary<long, SyncResult> ErpHierarchyResults { get; set; } = new();
        public Dictionary<long, SyncResult> ErpEmployeeResults { get; set; } = new();

        public void SetBpmDepartmentResult(long coId, SyncResult r) => BpmDepartmentResults[coId] = r;
        public void SetBpmHierarchyResult(long coId, SyncResult r) => BpmHierarchyResults[coId] = r;
        public void SetBpmEmployeeResult(long coId, SyncResult r) => BpmEmployeeResults[coId] = r;
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
            if (BpmOrganizationResult != null)
                sb.AppendLine($"  公司(Organization): {BpmOrganizationResult.SuccessCount}/{BpmOrganizationResult.TotalCount}");
            foreach (var kv in BpmDepartmentResults)
                sb.AppendLine($"  部門(CO_ID={kv.Key}): {kv.Value.SuccessCount}/{kv.Value.TotalCount}");
            foreach (var kv in BpmHierarchyResults)
                sb.AppendLine($"  層級(CO_ID={kv.Key}): {kv.Value.SuccessCount}/{kv.Value.TotalCount}");
            foreach (var kv in BpmEmployeeResults)
                sb.AppendLine($"  員工(CO_ID={kv.Key}): {kv.Value.SuccessCount}/{kv.Value.TotalCount}");
            sb.AppendLine();

            // ERP 結果
            sb.AppendLine("--- ERP 同步結果 ---");
            foreach (var kv in ErpDepartmentResults)
                sb.AppendLine($"  部門 gem_file(CO_ID={kv.Key}): {kv.Value.SuccessCount}/{kv.Value.TotalCount}");
            foreach (var kv in ErpHierarchyResults)
                sb.AppendLine($"  層級 abd_file(CO_ID={kv.Key}): {kv.Value.SuccessCount}/{kv.Value.TotalCount}");
            foreach (var kv in ErpEmployeeResults)
                sb.AppendLine($"  員工 gen_file(CO_ID={kv.Key}): {kv.Value.SuccessCount}/{kv.Value.TotalCount}");
            sb.AppendLine();

            // 錯誤詳情
            var allErrors = new List<string>();
            if (BpmOrganizationResult?.Errors.Count > 0) allErrors.AddRange(BpmOrganizationResult.Errors);
            foreach (var kv in BpmDepartmentResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in BpmHierarchyResults) allErrors.AddRange(kv.Value.Errors);
            foreach (var kv in BpmEmployeeResults) allErrors.AddRange(kv.Value.Errors);
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
