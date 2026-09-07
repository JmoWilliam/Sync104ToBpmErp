using System.Security.Cryptography;

namespace Sync104ToBpmErp.Models;

/// <summary>
/// OID 產生器 - 產生 32 位小寫十六進位字串，並確保系統中不重複。
///
/// 2026-09-03 修改：原本用 Guid.NewGuid() 產生完全隨機的 32 碼，跟 BPM 既有資料（Hibernate
/// uuid.hex 風格）比對後發現結構不同——BPM 原生 OID 是「前8碼同一批次內連續遞增、後24碼在
/// 同一次程式執行(JVM)期間固定不變」，我們原本是四段完全隨機、彼此無關聯。
/// 目前正在測試這個結構差異是否為「新同步進去的員工在 BPM 部分頁面顯示異常」的原因，
/// 改成同樣「前8碼流水號遞增、後24碼本次執行期間固定」的格式，方便比對測試。
/// 如果測試後證實無關，這裡可以改回單純的 Guid.NewGuid()。
/// </summary>
public static class OidHelper
{
    // 本次程式執行期間固定不變的 24 碼隨機值 (模擬 BPM 端同一 JVM 執行期間固定的那段)
    private static readonly string _sessionSuffix = GenerateSessionSuffix();

    // 本次程式執行期間遞增的計數器 (模擬 BPM 端同一批次內連續遞增的那段)，起始值隨機
    private static int _counter = GenerateRandomStart();

    private static string GenerateSessionSuffix()
    {
        var bytes = new byte[12]; // 12 bytes = 24 hex 字元
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int GenerateRandomStart()
    {
        var bytes = new byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF; // 限制在正數範圍
    }

    /// <summary>
    /// 產生 32 位小寫十六進位 OID：前8碼為本次執行期間遞增的計數器，後24碼為本次執行期間固定的隨機值
    /// </summary>
    public static string Generate()
    {
        var counter = Interlocked.Increment(ref _counter);
        return $"{(uint)counter:x8}{_sessionSuffix}";
    }

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
