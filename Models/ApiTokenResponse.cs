using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models
{
    /// <summary>
    /// HR API Token 回應資料模型 (data 部分)
    /// </summary>
    public class ApiTokenData
    {
        [JsonPropertyName("USER_ID")]
        public int UserId { get; set; }

        [JsonPropertyName("REFRESH_TOKEN")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("ACCESS_TOKEN")]
        public string AccessToken { get; set; } = string.Empty;
    }

    /// <summary>
    /// HR API Token 回應模型
    /// </summary>
    public class ApiTokenResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("data")]
        public ApiTokenData? Data { get; set; }
    }
}
