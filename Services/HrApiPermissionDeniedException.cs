namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// 104 HR Max API 回傳 490（帳號無此公司/此 API 存取權限）時拋出。
    /// 與一般連線/格式錯誤區分，讓呼叫端可以印出清楚的「沒有權限」訊息，
    /// 而不是完整的例外堆疊，並直接跳過該公司，不再嘗試後續同步步驟。
    /// </summary>
    public class HrApiPermissionDeniedException : Exception
    {
        public HrApiPermissionDeniedException(string message) : base(message) { }
    }
}
