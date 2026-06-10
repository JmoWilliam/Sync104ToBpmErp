using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models;

/// <summary>
/// 部門層級資料模型 (對應 104 HR API /api/os/dept_level)
/// </summary>
public class DeptHierarchy
{
    [JsonPropertyName("DEPT_LEVEL_ID")]
    public long DeptLevelId { get; set; }

    [JsonPropertyName("CO_ID")]
    public long CompanyId { get; set; }

    [JsonPropertyName("ORG_TYPE_CODE")]
    public string OrgTypeCode { get; set; } = string.Empty;

    [JsonPropertyName("ORG_TYPE_NAME")]
    public string OrgTypeName { get; set; } = string.Empty;

    [JsonPropertyName("LEVEL_NAME")]
    public string LevelName { get; set; } = string.Empty;

    [JsonPropertyName("LEVEL_NAME_JSON")]
    public object? LevelNameJson { get; set; }

    [JsonPropertyName("IS_ACT")]
    public int IsAct { get; set; }

    [JsonPropertyName("SORT_ORDER")]
    public double? SortOrder { get; set; }

    [JsonPropertyName("E_EMP_ID")]
    public long? ModifyEmpId { get; set; }

    [JsonPropertyName("E_EMP_NO")]
    public string? ModifyEmpNo { get; set; }

    [JsonPropertyName("E_DATETIME")]
    public string? LastModified { get; set; }

    // 以下屬性用於相容舊的資料庫同步程式碼
    // 注意：這些不是 API 回傳的欄位，而是資料庫同步時使用的欄位
    public string DeptCode { get; set; } = string.Empty;
    public string ParentDeptCode { get; set; } = string.Empty;
    public int Level { get; set; }
    public string Path { get; set; } = string.Empty;
}
