using Serilog;
using Serilog.Events;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// Serilog 日誌服務實作
    /// </summary>
    public class LoggerService : ILoggerService
    {
        private readonly ILogger _logger;
        private readonly ILogger _errorLogger;
        private readonly string _logDirectory;

        public LoggerService(string logDirectory)
        {
            _logDirectory = logDirectory;
            EnsureLogDirectoryExists();

            var logFilePath = Path.Combine(_logDirectory, $"SyncLog_{DateTime.Now:yyyyMMdd}.txt");
            var errorLogPath = Path.Combine(_logDirectory, $"ErrorLog_{DateTime.Now:yyyyMMdd}.txt");

            // 一般記錄 (Info 以上)
            _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(LogEventLevel.Information)
                .WriteTo.File(
                    logFilePath,
                    LogEventLevel.Information,
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)
                .CreateLogger();

            // 錯誤記錄 (Error 以上，包含同步失敗明細)
            _errorLogger = new LoggerConfiguration()
                .MinimumLevel.Error()
                .WriteTo.Console(LogEventLevel.Error)
                .WriteTo.File(
                    errorLogPath,
                    LogEventLevel.Error,
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)
                .CreateLogger();
        }

        private void EnsureLogDirectoryExists()
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
        }

        public void Info(string message)
        {
            _logger.Information(message);
        }

        public void Debug(string message)
        {
            _logger.Debug(message);
        }

        public void Warning(string message)
        {
            _logger.Warning(message);
        }

        public void Error(string message, Exception? exception = null)
        {
            if (exception != null)
            {
                _logger.Error(exception, message);
                _errorLogger.Error(exception, message);
            }
            else
            {
                _logger.Error(message);
                _errorLogger.Error(message);
            }
        }

        public void Error(string message, string details)
        {
            var fullMessage = $"{message} - Details: {details}";
            _logger.Error(fullMessage);
            _errorLogger.Error(fullMessage);
        }

        public void LogSyncStart(string dataType, DateTime startTime, DateTime endTime)
        {
            _logger.Information("========================================");
            _logger.Information($"[同步開始] {dataType} 資料");
            _logger.Information($"查詢時間範圍: {startTime:yyyy-MM-dd HH:mm:ss} ~ {endTime:yyyy-MM-dd HH:mm:ss}");
            _logger.Information("========================================");
        }

        public void LogSyncEnd(string dataType, int total, int success, int failed)
        {
            _logger.Information("========================================");
            _logger.Information($"[同步結束] {dataType} 資料");
            _logger.Information($"總筆數: {total}, 成功: {success}, 失敗: {failed}");
            if (failed > 0)
            {
                _logger.Warning($"注意: 有 {failed} 筆資料同步失敗，請查看 ErrorLog 了解詳情");
            }
            _logger.Information("========================================");
        }

        public void LogDbConnection(string dbName, bool success, string? errorMessage = null)
        {
            if (success)
            {
                _logger.Information($"[資料庫連線] {dbName} - 連線成功");
            }
            else
            {
                var msg = $"[資料庫連線] {dbName} - 連線失敗";
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    msg += $": {errorMessage}";
                }
                Error(msg);
            }
        }

        /// <summary>
        /// 記錄成功寫入資料庫的完整資料內容，供事後比對資料庫用
        /// 格式: tableName--&gt;Record data，一律以 Information 層級寫入 SyncLog 檔案
        /// </summary>
        public void LogSyncRecord(string tableName, string recordData)
        {
            _logger.Information($"{tableName}-->{recordData}");
        }

        public void LogSyncDetail(string dataType, string action, string key, bool success, string? errorMessage = null)
        {
            if (success)
            {
                // 成功的只在 Debug 層級記錄，避免 Log 過大
                _logger.Debug($"[同步成功] {dataType} | {action} | Key: {key}");
            }
            else
            {
                // 失敗的記錄到 Error Log
                var errorMsg = $"[同步失敗] {dataType} | {action} | Key: {key}";
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    errorMsg += $" | 錯誤: {errorMessage}";
                }
                Error(errorMsg);
            }
        }
    }
}
