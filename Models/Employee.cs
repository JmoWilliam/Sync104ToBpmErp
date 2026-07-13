using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models
{
    /// <summary>
    /// HR 員工資料模型 (對應 104 HR API /api/ed/emp)
    /// 對照表: api_erp_bpm_mapping.md §1.4
    /// </summary>
    public class Employee
    {
        [JsonPropertyName("EMP_ID")]
        public long EmpId { get; set; }

        [JsonPropertyName("CO_ID")]
        public long CompanyId { get; set; }

        [JsonPropertyName("CO_CODE")]
        public string CompanyCode { get; set; } = string.Empty;

        [JsonPropertyName("CO_NAME")]
        public string? CompanyName { get; set; }

        // ─── 基本資料 ───

        [JsonPropertyName("EMP_NO")]
        public string EmpNo { get; set; } = string.Empty;

        [JsonPropertyName("EMP_NAME")]
        public string EmpName { get; set; } = string.Empty;

        [JsonPropertyName("EMP_EN_NAME")]
        public string? EmpNameEn { get; set; }

        [JsonPropertyName("GENDER")]
        public int? Gender { get; set; }

        [JsonPropertyName("BIRTHDAY")]
        [JsonConverter(typeof(FlexibleNullableDateTimeConverter))]
        public DateTime? Birthday { get; set; }

        [JsonPropertyName("IDC_NO")]
        public string? IdcNo { get; set; }

        [JsonPropertyName("MARITAL_STATUS")]
        public int? MaritalStatus { get; set; }

        // ─── 部門 (104 支援 5 層組織) ───

        [JsonPropertyName("DEPT1_ID")]
        public long? Dept1Id { get; set; }

        [JsonPropertyName("DEPT1_CODE")]
        public string? Dept1Code { get; set; }

        [JsonPropertyName("DEPT1_NAME")]
        public string? Dept1Name { get; set; }

        [JsonPropertyName("DEPT2_ID")]
        public long? Dept2Id { get; set; }

        [JsonPropertyName("DEPT2_CODE")]
        public string? Dept2Code { get; set; }

        [JsonPropertyName("DEPT2_NAME")]
        public string? Dept2Name { get; set; }

        [JsonPropertyName("DEPT3_ID")]
        public long? Dept3Id { get; set; }

        [JsonPropertyName("DEPT3_CODE")]
        public string? Dept3Code { get; set; }

        [JsonPropertyName("DEPT3_NAME")]
        public string? Dept3Name { get; set; }

        [JsonPropertyName("DEPT4_ID")]
        public long? Dept4Id { get; set; }

        [JsonPropertyName("DEPT4_CODE")]
        public string? Dept4Code { get; set; }

        [JsonPropertyName("DEPT4_NAME")]
        public string? Dept4Name { get; set; }

        [JsonPropertyName("DEPT5_ID")]
        public long? Dept5Id { get; set; }

        [JsonPropertyName("DEPT5_CODE")]
        public string? Dept5Code { get; set; }

        [JsonPropertyName("DEPT5_NAME")]
        public string? Dept5Name { get; set; }

        // ─── 職務/職等/職級 ───

        [JsonPropertyName("JOB_ID")]
        public long? JobId { get; set; }

        [JsonPropertyName("JOB_CODE")]
        public string? JobCode { get; set; }

        [JsonPropertyName("JOB_NAME")]
        public string? JobName { get; set; }

        [JsonPropertyName("GRADE_ID")]
        public long? GradeId { get; set; }

        [JsonPropertyName("GRADE_CODE")]
        public string? GradeCode { get; set; }

        [JsonPropertyName("GRADE_NAME")]
        public string? GradeName { get; set; }

        [JsonPropertyName("LEVEL_ID")]
        public long? LevelId { get; set; }

        [JsonPropertyName("LEVEL_CODE")]
        public string? LevelCode { get; set; }

        [JsonPropertyName("LEVEL_NAME")]
        public string? LevelName { get; set; }

        [JsonPropertyName("JOB_CAT_ID")]
        public long? JobCatId { get; set; }

        [JsonPropertyName("JOB_CAT_CODE")]
        public string? JobCatCode { get; set; }

        [JsonPropertyName("JOB_CAT_NAME")]
        public string? JobCatName { get; set; }

        // ─── 聯絡資訊 ───

        [JsonPropertyName("OFFICE_EMAIL")]
        public string? Email { get; set; }

        [JsonPropertyName("PERSONAL_EMAIL")]
        public string? PersonalEmail { get; set; }

        [JsonPropertyName("OFFICE_TEL")]
        public string? Phone { get; set; }

        [JsonPropertyName("OFFICE_TEL_EXT")]
        public string? PhoneExt { get; set; }

        [JsonPropertyName("HOME_TEL")]
        public string? HomeTel { get; set; }

        [JsonPropertyName("MOBILE_TEL")]
        public string? MobileTel { get; set; }

        [JsonPropertyName("LIVE_ADDRESS")]
        public string? LiveAddress { get; set; }

        [JsonPropertyName("CONTACT_ADDRESS")]
        public string? ContactAddress { get; set; }

        // ─── 到職/離職 ───

        [JsonPropertyName("HIRE_DATE")]
        [JsonConverter(typeof(FlexibleNullableDateTimeConverter))]
        public DateTime? HireDate { get; set; }

        [JsonPropertyName("QUIT_DATE")]
        [JsonConverter(typeof(FlexibleNullableDateTimeConverter))]
        public DateTime? QuitDate { get; set; }

        [JsonPropertyName("WORK_STATUS")]
        public int? WorkStatus { get; set; }

        [JsonPropertyName("WORK_STATUS_NAME")]
        public string? WorkStatusName { get; set; }

        // ─── 向前相容 (舊程式碼會用到) ───
        // 注意: 這些不是 104 API 的實際欄位名稱，而是映射後的別名
        [JsonIgnore] public string DeptCode => Dept1Code ?? string.Empty;
        [JsonIgnore] public string? DeptName => Dept1Name;
        [JsonIgnore] public string? Position => JobName;
        [JsonIgnore] public DateTime? JoinDate => HireDate;
        [JsonIgnore] public DateTime? LeaveDate => QuitDate;
    }
}
