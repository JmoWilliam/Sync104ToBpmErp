using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models
{
    /// <summary>
    /// HR 部門資料模型 (對應 104 HR API /api/os/dept)
    /// </summary>
    public class Department
    {
        [JsonPropertyName("DEPT_ID")]
        public long DeptId { get; set; }

        [JsonPropertyName("CO_ID")]
        public long CompanyId { get; set; }

        [JsonPropertyName("ORG_TYPE_CODE")]
        public string OrgTypeCode { get; set; } = string.Empty;

        [JsonPropertyName("ORG_TYPE_NAME")]
        public string OrgTypeName { get; set; } = string.Empty;

        [JsonPropertyName("DEPT_CODE")]
        public string DeptCode { get; set; } = string.Empty;

        [JsonPropertyName("DEPT_NAME")]
        public string DeptName { get; set; } = string.Empty;

        [JsonPropertyName("DEPT_NAME_JSON")]
        public object? DeptNameJson { get; set; }

        [JsonPropertyName("DEPT_ABBR")]
        public string? DeptAbbr { get; set; }

        [JsonPropertyName("DEPT_ABBR_JSON")]
        public object? DeptAbbrJson { get; set; }

        [JsonPropertyName("DEPT_LEVEL_ID")]
        public long? DeptLevelId { get; set; }

        [JsonPropertyName("DEPT_LEVEL_NAME")]
        public string? DeptLevelName { get; set; }

        [JsonPropertyName("LEADER_ID")]
        public long? LeaderId { get; set; }

        [JsonPropertyName("LEADER_EMP_NO")]
        public string? LeaderEmpNo { get; set; }

        [JsonPropertyName("LEADER_EMP_NAME")]
        public string? LeaderEmpName { get; set; }

        [JsonPropertyName("DEPUTY_LEADER_ID")]
        public long? DeputyLeaderId { get; set; }

        [JsonPropertyName("DEPUTY_LEADER_EMP_NO")]
        public string? DeputyLeaderEmpNo { get; set; }

        [JsonPropertyName("DEPUTY_LEADER_EMP_NAME")]
        public string? DeputyLeaderEmpName { get; set; }

        [JsonPropertyName("POSTAL_CODE")]
        public string? PostalCode { get; set; }

        [JsonPropertyName("ADDRESS")]
        public string? Address { get; set; }

        [JsonPropertyName("TEL")]
        public string? Tel { get; set; }

        [JsonPropertyName("FAX")]
        public string? Fax { get; set; }

        [JsonPropertyName("IS_ACT")]
        public int IsAct { get; set; }

        [JsonPropertyName("PARENT_DEPT_ID")]
        public long? ParentDeptId { get; set; }

        [JsonPropertyName("PARENT_DEPT_CODE")]
        public string? ParentDeptCode { get; set; }

        [JsonPropertyName("PARENT_DEPT_NAME")]
        public string? ParentDeptName { get; set; }

        [JsonPropertyName("DEPT_RELATION")]
        public string? DeptRelation { get; set; }

        [JsonPropertyName("DEPT_START_DATE")]
        [JsonConverter(typeof(FlexibleNullableDateTimeConverter))]
        public DateTime? DeptStartDate { get; set; }

        [JsonPropertyName("E_EMP_ID")]
        public long? ModifyEmpId { get; set; }

        [JsonPropertyName("E_EMP_NO")]
        public string? ModifyEmpNo { get; set; }

        [JsonPropertyName("E_DATETIME")]
        public string? LastModified { get; set; }
    }
}
