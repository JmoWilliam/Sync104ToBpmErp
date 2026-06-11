namespace Sync104ToBpmErp.Models;

/// <summary>
/// OID 產生器 - 產生 32 位數英數字亂碼，並確保系統中不重複
/// </summary>
public static class OidHelper
{
    private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly Random _random = new();

    /// <summary>
    /// 產生 32 位數英數字亂碼
    /// </summary>
    public static string Generate()
    {
        var oid = new char[32];
        for (int i = 0; i < 32; i++)
            oid[i] = Chars[_random.Next(Chars.Length)];
        return new string(oid);
    }

    /// <summary>
    /// 產生不會與指定查詢衝突的 OID
    /// </summary>
    /// <param name="checkExistsAsync">非同步委派，傳入 OID 字串回傳是否存在</param>
    /// <param name="maxAttempts">最大嘗試次數（避免無限迴圈）</param>
    public static async Task<string> GenerateUniqueAsync(Func<string, Task<bool>> checkExistsAsync, int maxAttempts = 100)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var oid = Generate();
            var exists = await checkExistsAsync(oid);
            if (!exists)
                return oid;
        }
        throw new InvalidOperationException($"無法產生唯一的 OID（已嘗試 {maxAttempts} 次）");
    }
}
