namespace Sync104ToBpmErp.Models;

/// <summary>
/// OID 產生器 - 產生 32 位小寫十六進位字串（Guid），並確保系統中不重複
/// </summary>
public static class OidHelper
{
    /// <summary>
    /// 產生 32 位小寫十六進位 OID (Guid.NewGuid().ToString("N"))
    /// 使用 Guid 取代 Random，確保唯一性且執行緒安全
    /// </summary>
    public static string Generate() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 產生不會與指定查詢衝突的 OID
    /// </summary>
    /// <param name="checkExistsAsync">非同步委派，傳入 OID 字串回傳是否存在</param>
    /// <param name="maxAttempts">最大嘗試次數（避免無限迴圈）</param>
    public static async Task<string> GenerateUniqueAsync(Func<string, Task<bool>> checkExistsAsync, int maxAttempts = 10)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var oid = Generate();
            if (!await checkExistsAsync(oid))
                return oid;
        }
        throw new InvalidOperationException($"無法產生唯一的 OID（已嘗試 {maxAttempts} 次）");
    }
}
