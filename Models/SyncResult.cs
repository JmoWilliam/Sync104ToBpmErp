namespace Sync104ToBpmErp.Models
{
    /// <summary>
    /// 同步結果模型
    /// </summary>
    public class SyncResult
    {
        public bool Success { get; set; }
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public DateTime SyncTime { get; set; } = DateTime.Now;
        public string DataType { get; set; } = string.Empty;
        public string TargetSystem { get; set; } = string.Empty;
    }
}
