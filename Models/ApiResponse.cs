using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models;

/// <summary>
/// 104 HR API 通用回應包裝類別
/// </summary>
public class ApiResponse<T>
{
    /// <summary>
    /// 回應碼 (200 表示成功)
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    /// 回應訊息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 回應資料
    /// </summary>
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess => Code == 200;
}

/// <summary>
/// 簡化版 API 回應 (無資料)
/// </summary>
public class ApiResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    public bool IsSuccess => Code == 200;
}
