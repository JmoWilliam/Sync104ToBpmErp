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
        /// 執行完整同步流程
        /// </summary>
        /// <param name="startTime">開始時間</param>
        /// <param name="endTime">截止時間</param>
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

                // 1. 同步員工資料
                await SyncEmployeesAsync(startTime, endTime, report);

                // 2. 同步部門資料
                await SyncDepartmentsAsync(startTime, endTime, report);

                // 3. 同步部門層級資料
                await SyncDeptHierarchyAsync(report);

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
        /// 同步員工資料
        /// </summary>
        private async Task SyncEmployeesAsync(DateTime startTime, DateTime endTime, SyncReport report)
        {
            _logger.LogSyncStart("Employee", startTime, endTime);

            try
            {
                // 從 HR API 取得員工資料
                var employees = await _hrApiService.GetEmployeesAsync(startTime, endTime);

                if (employees.Count == 0)
                {
                    _logger.Warning("[HR API] 回傳的員工資料為空");
                    return;
                }

                _logger.Info($"[同步處理] 開始同步 {employees.Count} 筆員工資料到 BPM 和 ERP...");

                // 同步到 BPM
                _logger.Info("[同步處理] 正在同步到 BPM...");
                var bpmResult = await _bpmDatabaseService.SyncEmployeesAsync(employees);
                report.BpmEmployeeResult = bpmResult;

                // 同步到 ERP
                _logger.Info("[同步處理] 正在同步到 ERP...");
                var erpResult = await _erpDatabaseService.SyncEmployeesAsync(employees);
                report.ErpEmployeeResult = erpResult;

                // 總結
                var totalSuccess = bpmResult.SuccessCount + erpResult.SuccessCount;
                var totalFailed = bpmResult.FailedCount + erpResult.FailedCount;
                _logger.Info($"[同步完成] 員工資料同步完成 - BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, ERP: {erpResult.SuccessCount}/{erpResult.TotalCount}");
            }
            catch (Exception ex)
            {
                _logger.Error("[同步錯誤] 同步員工資料時發生錯誤", ex);
                throw;
            }
        }

        /// <summary>
        /// 同步部門資料
        /// </summary>
        private async Task SyncDepartmentsAsync(DateTime startTime, DateTime endTime, SyncReport report)
        {
            _logger.LogSyncStart("Department", startTime, endTime);

            try
            {
                // 從 HR API 取得部門資料
                var departments = await _hrApiService.GetDepartmentsAsync(startTime, endTime);

                if (departments.Count == 0)
                {
                    _logger.Warning("[HR API] 回傳的部門資料為空");
                    return;
                }

                _logger.Info($"[同步處理] 開始同步 {departments.Count} 筆部門資料到 BPM 和 ERP...");

                // 同步到 BPM
                _logger.Info("[同步處理] 正在同步到 BPM...");
                var bpmResult = await _bpmDatabaseService.SyncDepartmentsAsync(departments);
                report.BpmDepartmentResult = bpmResult;

                // 同步到 ERP
                _logger.Info("[同步處理] 正在同步到 ERP...");
                var erpResult = await _erpDatabaseService.SyncDepartmentsAsync(departments);
                report.ErpDepartmentResult = erpResult;

                // 總結
                _logger.Info($"[同步完成] 部門資料同步完成 - BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, ERP: {erpResult.SuccessCount}/{erpResult.TotalCount}");
            }
            catch (Exception ex)
            {
                _logger.Error("[同步錯誤] 同步部門資料時發生錯誤", ex);
                throw;
            }
        }

        /// <summary>
        /// 同步部門層級資料
        /// </summary>
        private async Task SyncDeptHierarchyAsync(SyncReport report)
        {
            _logger.Info("[同步開始] DeptHierarchy 資料 (全量同步)");

            try
            {
                // 從 HR API 取得部門層級資料
                var hierarchy = await _hrApiService.GetDeptHierarchyAsync();

                if (hierarchy.Count == 0)
                {
                    _logger.Warning("[HR API] 回傳的部門層級資料為空");
                    return;
                }

                _logger.Info($"[同步處理] 開始同步 {hierarchy.Count} 筆部門層級資料到 BPM 和 ERP...");

                // 同步到 BPM
                _logger.Info("[同步處理] 正在同步到 BPM...");
                var bpmResult = await _bpmDatabaseService.SyncDeptHierarchyAsync(hierarchy);
                report.BpmHierarchyResult = bpmResult;

                // 同步到 ERP
                _logger.Info("[同步處理] 正在同步到 ERP...");
                var erpResult = await _erpDatabaseService.SyncDeptHierarchyAsync(hierarchy);
                report.ErpHierarchyResult = erpResult;

                // 總結
                _logger.Info($"[同步完成] 部門層級同步完成 - BPM: {bpmResult.SuccessCount}/{bpmResult.TotalCount}, ERP: {erpResult.SuccessCount}/{erpResult.TotalCount}");
            }
            catch (Exception ex)
            {
                _logger.Error("[同步錯誤] 同步部門層級資料時發生錯誤", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// 同步報告
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

        public SyncResult? BpmEmployeeResult { get; set; }
        public SyncResult? BpmDepartmentResult { get; set; }
        public SyncResult? BpmHierarchyResult { get; set; }

        public SyncResult? ErpEmployeeResult { get; set; }
        public SyncResult? ErpDepartmentResult { get; set; }
        public SyncResult? ErpHierarchyResult { get; set; }

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
            if (BpmEmployeeResult != null)
                sb.AppendLine($"員工: 總計 {BpmEmployeeResult.TotalCount}, 成功 {BpmEmployeeResult.SuccessCount}, 失敗 {BpmEmployeeResult.FailedCount}");
            if (BpmDepartmentResult != null)
                sb.AppendLine($"部門: 總計 {BpmDepartmentResult.TotalCount}, 成功 {BpmDepartmentResult.SuccessCount}, 失敗 {BpmDepartmentResult.FailedCount}");
            if (BpmHierarchyResult != null)
                sb.AppendLine($"層級: 總計 {BpmHierarchyResult.TotalCount}, 成功 {BpmHierarchyResult.SuccessCount}, 失敗 {BpmHierarchyResult.FailedCount}");
            sb.AppendLine();

            // ERP 結果
            sb.AppendLine("--- ERP 同步結果 ---");
            if (ErpEmployeeResult != null)
                sb.AppendLine($"員工: 總計 {ErpEmployeeResult.TotalCount}, 成功 {ErpEmployeeResult.SuccessCount}, 失敗 {ErpEmployeeResult.FailedCount}");
            if (ErpDepartmentResult != null)
                sb.AppendLine($"部門: 總計 {ErpDepartmentResult.TotalCount}, 成功 {ErpDepartmentResult.SuccessCount}, 失敗 {ErpDepartmentResult.FailedCount}");
            if (ErpHierarchyResult != null)
                sb.AppendLine($"層級: 總計 {ErpHierarchyResult.TotalCount}, 成功 {ErpHierarchyResult.SuccessCount}, 失敗 {ErpHierarchyResult.FailedCount}");
            sb.AppendLine();

            // 錯誤詳情
            var allErrors = new List<string>();
            if (BpmEmployeeResult?.Errors.Count > 0) allErrors.AddRange(BpmEmployeeResult.Errors);
            if (BpmDepartmentResult?.Errors.Count > 0) allErrors.AddRange(BpmDepartmentResult.Errors);
            if (BpmHierarchyResult?.Errors.Count > 0) allErrors.AddRange(BpmHierarchyResult.Errors);
            if (ErpEmployeeResult?.Errors.Count > 0) allErrors.AddRange(ErpEmployeeResult.Errors);
            if (ErpDepartmentResult?.Errors.Count > 0) allErrors.AddRange(ErpDepartmentResult.Errors);
            if (ErpHierarchyResult?.Errors.Count > 0) allErrors.AddRange(ErpHierarchyResult.Errors);

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
