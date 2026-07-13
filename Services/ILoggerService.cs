namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// 日誌服務介面
    /// </summary>
    public interface ILoggerService
    {
        void Info(string message);
        void Debug(string message);
        void Warning(string message);
        void Error(string message, Exception? exception = null);
        void Error(string message, string details);
        void LogSyncStart(string dataType, DateTime startTime, DateTime endTime);
        void LogSyncEnd(string dataType, int total, int success, int failed);
        void LogDbConnection(string dbName, bool success, string? errorMessage = null);
        void LogSyncDetail(string dataType, string action, string key, bool success, string? errorMessage = null);
        void LogSyncRecord(string tableName, string recordData);
    }
}
