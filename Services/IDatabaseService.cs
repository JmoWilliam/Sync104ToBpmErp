using System.Data;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// 資料庫服務介面
    /// </summary>
    public interface IDatabaseService
    {
        /// <summary>
        /// 測試資料庫連線
        /// </summary>
        Task<bool> TestConnectionAsync();

        /// <summary>
        /// 取得資料庫名稱
        /// </summary>
        string GetDatabaseName();

        // ─── BPM Organization（公司） ───

        /// <summary>
        /// BPM: 同步公司資料到 Organization 表
        /// </summary>
        Task<SyncResult> SyncOrganizationAsync(List<CompanyInfo> companies);

        /// <summary>
        /// BPM: 同步部門資料到 OrganizationUnit 表
        /// </summary>
        Task<SyncResult> SyncOrganizationUnitsAsync(List<Department> departments, long coId, string coCode);

        /// <summary>
        /// BPM: 同步部門層級資料到 OrganizationUnitLevel 表
        /// </summary>
        Task<SyncResult> SyncOrganizationUnitLevelsAsync(List<DeptHierarchy> hierarchy, long coId, string coCode);

        /// <summary>
        /// BPM: 同步員工資料到 Users + Employee 表
        /// </summary>
        Task<SyncResult> SyncEmployeesAsync(List<Employee> employees, long coId);

        /// <summary>
        /// BPM: 查詢每位員工所屬部門 (OrganizationUnit.managerOID) 對應的主管員工編號，
        /// 供 ERP gen_file.TA_GEN07 使用。Key=EMP_NO，查無主管時不會出現在回傳字典中。
        /// </summary>
        Task<Dictionary<string, string>> GetEmployeeManagerEmpNosAsync(List<Employee> employees);

        // ─── ERP ───

        /// <summary>
        /// ERP: 同步部門資料到 gem_file
        /// </summary>
        Task<SyncResult> SyncGemFileAsync(List<Department> departments);

        /// <summary>
        /// ERP: 同步部門層級資料到 abd_file (從 dept_level API，已棄用)
        /// </summary>
        Task<SyncResult> SyncAbdFileAsync(List<DeptHierarchy> hierarchy);

        /// <summary>
        /// ERP: 同步部門層級關係到 abd_file (從 Department.ParentDeptCode)
        /// </summary>
        Task<SyncResult> SyncAbdFileFromDepartmentsAsync(List<Department> departments);

        /// <summary>
        /// ERP: 同步員工資料到 gen_file
        /// managerEmpNoMap: EMP_NO -> 直屬主管工號 (來自 BPM GetEmployeeManagerEmpNosAsync)
        /// </summary>
        Task<SyncResult> SyncGenFileAsync(List<Employee> employees, Dictionary<string, string>? managerEmpNoMap = null);
    }
}
