using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;
using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;
using Sync104ToBpmErp.Services;

namespace Sync104ToBpmErp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 強制關閉 ODP.NET 的 TNS 名稱解析
            // 避免在沒有 tnsnames.ora 的環境下 EzConnect 格式被錯誤解析成 TNS alias
            OracleConfiguration.TnsAdmin = "";

            Console.WriteLine("========================================");
            Console.WriteLine("HR 資料同步排程程式");
            Console.WriteLine("Sync104ToBpmErp v1.0");
            Console.WriteLine("========================================");
            Console.WriteLine();

            try
            {
                // 載入設定檔
                var configuration = LoadConfiguration();
                var appSettings = configuration.Get<AppSettings>();

                if (appSettings == null)
                {
                    Console.WriteLine("錯誤: 無法載入 appsettings.json 設定檔");
                    Environment.Exit(1);
                    return;
                }

                // 初始化日誌服務
                var logDirectory = appSettings.SyncSettings.LogDirectory;
                if (!Path.IsPathRooted(logDirectory))
                {
                    logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logDirectory);
                }
                var logger = new LoggerService(logDirectory);

                logger.Info("[程式啟動] HR 資料同步程式啟動");
                logger.Info($"[程式資訊] Log 目錄: {logDirectory}");

                // 檢查命令列參數
                if (args.Length > 0)
                {
                    switch (args[0].ToLower())
                    {
                        case "--help":
                        case "-h":
                        case "/?":
                            ShowHelp();
                            return;

                        case "--test-connection":
                        case "-t":
                            await TestConnectionsAsync(appSettings, logger);
                            return;

                        case "--test-api":
                        case "-a":
                            await TestHRApiAsync(appSettings, logger);
                            return;

                        case "--sync":
                        case "-s":
                            // 檢查是否有時間參數
                            if (args.Length >= 3)
                            {
                                await RunSyncWithArgsAsync(appSettings, logger, args);
                            }
                            else
                            {
                                Console.WriteLine("錯誤: 同步模式需要提供開始時間和截止時間");
                                Console.WriteLine("使用方式: Sync104ToBpmErp.exe --sync \"2024-01-01 00:00:00\" \"2024-12-31 23:59:59\"");
                                Console.WriteLine("或互動式輸入: Sync104ToBpmErp.exe --sync");
                            }
                            return;

                        default:
                            Console.WriteLine($"未知的參數: {args[0]}");
                            Console.WriteLine("使用 --help 查看說明");
                            return;
                    }
                }

                // 預設互動模式 - 詢問時間參數
                await RunInteractiveModeAsync(appSettings, logger);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"程式執行時發生未預期的錯誤: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// 載入設定檔
        /// </summary>
        static IConfiguration LoadConfiguration()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(basePath, "appsettings.json");

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"警告: 找不到 appsettings.json，使用預設路徑: {configPath}");
            }

            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
        }

        /// <summary>
        /// 顯示說明
        /// </summary>
        static void ShowHelp()
        {
            Console.WriteLine("使用方式:");
            Console.WriteLine("  Sync104ToBpmErp [選項] [參數]");
            Console.WriteLine();
            Console.WriteLine("選項:");
            Console.WriteLine("  --help, -h, /?      顯示此說明");
            Console.WriteLine("  --test-connection, -t  測試資料庫連線");
            Console.WriteLine("  --test-api, -a      測試 HR API (不連資料庫)");
            Console.WriteLine("  --sync, -s          執行資料同步 (需要時間參數)");
            Console.WriteLine();
            Console.WriteLine("同步模式:");
            Console.WriteLine("  方式 1 - 命令列參數:");
            Console.WriteLine("    Sync104ToBpmErp.exe --sync \"2024-01-01 00:00:00\" \"2024-12-31 23:59:59\"");
            Console.WriteLine();
            Console.WriteLine("  方式 2 - 互動式輸入:");
            Console.WriteLine("    Sync104ToBpmErp.exe");
            Console.WriteLine("    (程式會提示輸入開始時間和截止時間)");
            Console.WriteLine();
            Console.WriteLine("時間格式:");
            Console.WriteLine("  yyyy-MM-dd HH:mm:ss  (例如: 2024-01-15 09:30:00)");
            Console.WriteLine("  yyyy-MM-dd           (例如: 2024-01-15，會自動轉為 00:00:00)");
            Console.WriteLine();
            Console.WriteLine("設定檔:");
            Console.WriteLine("  appsettings.json    包含 HR API、資料庫連線等設定");
            Console.WriteLine();
            Console.WriteLine("Log 檔案:");
            Console.WriteLine("  Logs/SyncLog_YYYYMMDD.txt    一般執行記錄");
            Console.WriteLine("  Logs/ErrorLog_YYYYMMDD.txt   錯誤記錄 (包含同步失敗明細)");
        }

        /// <summary>
        /// 互動模式 - 詢問使用者輸入時間
        /// </summary>
        static async Task RunInteractiveModeAsync(AppSettings settings, ILoggerService logger)
        {
            Console.WriteLine("[互動模式] 請輸入同步時間範圍");
            Console.WriteLine();

            // 輸入開始時間
            DateTime startTime;
            while (true)
            {
                Console.Write("開始時間 (yyyy-MM-dd HH:mm:ss): ");
                var input = Console.ReadLine();
                if (TryParseDateTime(input, out startTime))
                {
                    break;
                }
                Console.WriteLine("  錯誤: 時間格式不正確，請重新輸入");
            }

            // 輸入截止時間
            DateTime endTime;
            while (true)
            {
                Console.Write("截止時間 (yyyy-MM-dd HH:mm:ss): ");
                var input = Console.ReadLine();
                if (TryParseDateTime(input, out endTime))
                {
                    if (endTime >= startTime)
                    {
                        break;
                    }
                    Console.WriteLine("  錯誤: 截止時間必須大於或等於開始時間");
                }
                else
                {
                    Console.WriteLine("  錯誤: 時間格式不正確，請重新輸入");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"確認執行同步:");
            Console.WriteLine($"  開始時間: {startTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  截止時間: {endTime:yyyy-MM-dd HH:mm:ss}");
            Console.Write("是否繼續? (Y/N): ");

            var confirm = Console.ReadLine()?.Trim().ToUpper();
            if (confirm != "Y" && confirm != "YES")
            {
                Console.WriteLine("已取消同步");
                return;
            }

            Console.WriteLine();
            await ExecuteSyncAsync(settings, logger, startTime, endTime);
        }

        /// <summary>
        /// 使用命令列參數執行同步
        /// </summary>
        static async Task RunSyncWithArgsAsync(AppSettings settings, ILoggerService logger, string[] args)
        {
            // 解析時間參數
            if (!TryParseDateTime(args[1], out DateTime startTime))
            {
                Console.WriteLine($"錯誤: 開始時間格式不正確: {args[1]}");
                Console.WriteLine("正確格式: yyyy-MM-dd HH:mm:ss 或 yyyy-MM-dd");
                Environment.Exit(1);
                return;
            }

            if (!TryParseDateTime(args[2], out DateTime endTime))
            {
                Console.WriteLine($"錯誤: 截止時間格式不正確: {args[2]}");
                Console.WriteLine("正確格式: yyyy-MM-dd HH:mm:ss 或 yyyy-MM-dd");
                Environment.Exit(1);
                return;
            }

            if (endTime < startTime)
            {
                Console.WriteLine("錯誤: 截止時間必須大於或等於開始時間");
                Environment.Exit(1);
                return;
            }

            await ExecuteSyncAsync(settings, logger, startTime, endTime);
        }

        /// <summary>
        /// 嘗試解析日期時間字串
        /// </summary>
        static bool TryParseDateTime(string? input, out DateTime result)
        {
            result = DateTime.MinValue;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.Trim();

            // 嘗試完整格式 yyyy-MM-dd HH:mm:ss
            if (DateTime.TryParseExact(input, "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result))
            {
                return true;
            }

            // 嘗試日期格式 yyyy-MM-dd，自動補上 00:00:00
            if (DateTime.TryParseExact(input, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dateResult))
            {
                result = dateResult;
                return true;
            }

            // 嘗試預設解析
            if (DateTime.TryParse(input, out result))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 執行同步
        /// </summary>
        static async Task ExecuteSyncAsync(AppSettings settings, ILoggerService logger, DateTime startTime, DateTime endTime)
        {
            // 建立服務實例
            var hrService = new HRApiService(settings.HRApi, logger);
            var bpmService = new BpmDatabaseService(settings.BpmDatabase, logger, settings.SyncSettings.BatchSize);
            var erpService = new ErpDatabaseService(settings.ErpDatabase, logger, settings.SyncSettings.BatchSize);

            // 建立同步服務
            var syncService = new SyncService(hrService, bpmService, erpService, logger);

            // 執行同步
            var report = await syncService.RunFullSyncAsync(startTime, endTime);

            // 輸出報告到主控台
            Console.WriteLine();
            Console.WriteLine(report.GenerateTextReport());

            // 根據結果設定結束代碼
            Environment.Exit(report.Success ? 0 : 1);
        }

        /// <summary>
        /// 測試 HR API (不連資料庫)
        /// 1. 從 /api/os/company 取得公司清單 (CO_ID, CO_CODE)
        /// 2. 對每家公司執行員工/部門/部門層級 API 測試
        /// </summary>
        static async Task TestHRApiAsync(AppSettings settings, ILoggerService logger)
        {
            Console.WriteLine("[API 測試] 正在測試 HR API 連線...");
            Console.WriteLine();

            // 只建立 HR API 服務，不需要資料庫
            var hrService = new HRApiService(settings.HRApi, logger);
            var hasError = false;

            try
            {
                // 1. 測試取得 Access Token
                Console.WriteLine("1. 測試取得 Access Token...");
                var token = await hrService.GetAccessTokenAsync();
                Console.WriteLine($"   ✓ Token 取得成功: {token.Substring(0, Math.Min(20, token.Length))}...");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ 取得 Token 失敗: {ex.Message}");
                logger.Error("API 測試 - 取得 Token 失敗", ex);
                hasError = true;
                Console.WriteLine();
                Console.WriteLine("[API 測試] 無法取得 Token，後續測試無法繼續 ⚠️");
                return;
            }

            // 2. 從 /api/os/company 取得公司清單
            List<CompanyInfo> companies;
            try
            {
                Console.WriteLine("2. 取得公司清單 (/api/os/company)...");
                companies = await hrService.GetCompaniesAsync();
                Console.WriteLine($"   ✓ 取得 {companies.Count} 筆公司資料");
                foreach (var c in companies)
                {
                    Console.WriteLine($"     CO_ID={c.CompanyId}, CO_CODE={c.CompanyCode}, CO_NAME={c.CompanyName}");
                }
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ 取得公司清單失敗: {ex.Message}");
                logger.Error("API 測試 - 取得公司清單失敗", ex);
                Console.WriteLine();
                Console.WriteLine("[API 測試] 無法取得公司清單，後續測試無法繼續 ⚠️");
                return;
            }

            if (companies.Count == 0)
            {
                Console.WriteLine("[API 測試] 公司清單為空，無資料可測試 ⚠️");
                return;
            }

            // 設定時間範圍 (最近一年)
            var endTime = DateTime.Now;
            var startTime = endTime.AddDays(-365);

            // 3. 依公司 loop 測試各 API
            foreach (var company in companies)
            {
                var coId = company.CompanyId;
                Console.WriteLine($"========================================");
                Console.WriteLine($"處理公司: CO_ID={coId}, CO_CODE={company.CompanyCode}, CO_NAME={company.CompanyName}");
                Console.WriteLine($"========================================");

                // 3.1 測試取得員工資料
                try
                {
                    Console.WriteLine($"3.1 測試取得員工資料 (CO_ID={coId}, {startTime:yyyy-MM-dd} ~ {endTime:yyyy-MM-dd})...");
                    var employees = await hrService.GetEmployeesAsync(startTime, endTime, coId);
                    Console.WriteLine($"   ✓ 取得 {employees.Count} 筆員工資料");
                    if (employees.Count > 0)
                    {
                        Console.WriteLine($"   第一筆: {employees[0].EmpNo} - {employees[0].EmpName} ({employees[0].DeptName})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ✗ 取得員工資料失敗 (CO_ID={coId}): {ex.Message}");
                    logger.Error($"API 測試 - 取得員工資料失敗 (CO_ID={coId})", ex);
                    hasError = true;
                }
                Console.WriteLine();

                // 3.2 測試取得部門資料
                try
                {
                    Console.WriteLine($"3.2 測試取得部門資料 (CO_ID={coId}, {startTime:yyyy-MM-dd} ~ {endTime:yyyy-MM-dd})...");
                    var departments = await hrService.GetDepartmentsAsync(startTime, endTime, coId);
                    Console.WriteLine($"   ✓ 取得 {departments.Count} 筆部門資料");
                    if (departments.Count > 0)
                    {
                        Console.WriteLine($"   第一筆: {departments[0].DeptCode} - {departments[0].DeptName}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ✗ 取得部門資料失敗 (CO_ID={coId}): {ex.Message}");
                    logger.Error($"API 測試 - 取得部門資料失敗 (CO_ID={coId})", ex);
                    hasError = true;
                }
                Console.WriteLine();

                // 3.3 測試取得部門層級資料
                try
                {
                    Console.WriteLine($"3.3 測試取得部門層級資料 (CO_ID={coId})...");
                    var hierarchy = await hrService.GetDeptHierarchyAsync(coId);
                    Console.WriteLine($"   ✓ 取得 {hierarchy.Count} 筆部門層級資料");
                    if (hierarchy.Count > 0)
                    {
                        Console.WriteLine($"   第一筆: {hierarchy[0].LevelName} (ID: {hierarchy[0].DeptLevelId})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ✗ 取得部門層級資料失敗 (CO_ID={coId}): {ex.Message}");
                    logger.Error($"API 測試 - 取得部門層級資料失敗 (CO_ID={coId})", ex);
                    hasError = true;
                }
                Console.WriteLine();
            }

            // 總結結果
            Console.WriteLine("========================================");
            if (hasError)
            {
                Console.WriteLine("[API 測試] 部分測試失敗，請查看上方詳細資訊 ⚠️");
            }
            else
            {
                Console.WriteLine("[API 測試] 所有測試通過 ✓");
            }
        }

        /// <summary>
        /// 測試資料庫連線
        /// </summary>
        static async Task TestConnectionsAsync(AppSettings settings, ILoggerService logger)
        {
            Console.WriteLine("[連線測試] 正在測試資料庫連線...");
            Console.WriteLine();

            // 測試 BPM 資料庫
            var bpmService = new BpmDatabaseService(settings.BpmDatabase, logger);
            var bpmConnected = await bpmService.TestConnectionAsync();
            Console.WriteLine($"BPM 資料庫 (MS-SQL): {(bpmConnected ? "連線成功 ✓" : "連線失敗 ✗")}");

            // 測試 ERP 資料庫
            var erpService = new ErpDatabaseService(settings.ErpDatabase, logger);
            var erpConnected = await erpService.TestConnectionAsync();
            Console.WriteLine($"ERP 資料庫 (Oracle): {(erpConnected ? "連線成功 ✓" : "連線失敗 ✗")}");

            // 測試 HR API
            Console.WriteLine();
            try
            {
                var hrService = new HRApiService(settings.HRApi, logger);
                var token = await hrService.GetAccessTokenAsync();
                Console.WriteLine($"HR API: 連線成功 ✓ (Token 取得成功)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HR API: 連線失敗 ✗ - {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("[連線測試] 測試完成");
        }
    }
}
