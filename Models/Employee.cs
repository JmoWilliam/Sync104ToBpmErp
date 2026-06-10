using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models
{
    /// <summary>
    /// HR 員工資料模型 (對應 104 HR API)
    /// </summary>
    public class Employee
    {
        [JsonPropertyName("EMP_ID")]
        public int EmpId { get; set; }

        [JsonPropertyName("CO_ID")]
        public int CompanyId { get; set; }

        [JsonPropertyName("CO_CODE")]
        public string CompanyCode { get; set; } = string.Empty;

        [JsonPropertyName("CO_NAME")]
        public string CompanyName { get; set; } = string.Empty;

        [JsonPropertyName("EMP_NO")]
        public string EmpNo { get; set; } = string.Empty;

        [JsonPropertyName("EMP_NAME")]
        public string EmpName { get; set; } = string.Empty;

        [JsonPropertyName("EMP_EN_NAME")]
        public string? EmpNameEn { get; set; }

        [JsonPropertyName("DEPT_CODE")]
        public string DeptCode { get; set; } = string.Empty;

        [JsonPropertyName("DEPT_NAME")]
        public string? DeptName { get; set; }

        [JsonPropertyName("POSITION")]
        public string? Position { get; set; }

        [JsonPropertyName("EMAIL")]
        public string? Email { get; set; }

        [JsonPropertyName("PHONE")]
        public string? Phone { get; set; }

        [JsonPropertyName("STATUS")]
        public string? Status { get; set; }

        [JsonPropertyName("JOIN_DATE")]
        public DateTime? JoinDate { get; set; }

        [JsonPropertyName("LEAVE_DATE")]
        public DateTime? LeaveDate { get; set; }

        [JsonPropertyName("MANAGER_EMP_NO")]
        public string? ManagerEmpNo { get; set; }

        [JsonPropertyName("LAST_MODIFIED")]
        public DateTime? LastModified { get; set; }
    }
}
