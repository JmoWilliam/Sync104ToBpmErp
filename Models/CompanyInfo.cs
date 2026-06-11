using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models;

/// <summary>
/// 104 HR API 公司資訊回應模型 (對應 /api/os/company)
/// </summary>
public class CompanyInfo
{
    [JsonPropertyName("CO_ID")]
    public long CompanyId { get; set; }

    [JsonPropertyName("CO_CODE")]
    public string CompanyCode { get; set; } = string.Empty;

    [JsonPropertyName("CO_NAME")]
    public string CompanyName { get; set; } = string.Empty;

    [JsonPropertyName("CO_NAME_JSON")]
    public object? CompanyNameJson { get; set; }

    [JsonPropertyName("SORT_ORDER")]
    public double? SortOrder { get; set; }

    [JsonPropertyName("BUILD_DATE")]
    public string? BuildDate { get; set; }

    [JsonPropertyName("DEF_LANG")]
    public string? DefaultLang { get; set; }

    [JsonPropertyName("TIME_ZONE")]
    public string? TimeZone { get; set; }

    [JsonPropertyName("IS_ACT")]
    public int IsAct { get; set; }
}
