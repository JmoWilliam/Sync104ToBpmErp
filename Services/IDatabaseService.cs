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
        /// 同步員工資料到目標資料庫
        /// </summary>
        Task<SyncResult> SyncEmployeesAsync(List<Employee> employees);

        /// <summary>
        /// 同步部門資料到目標資料庫
        /// </summary>
        Task<SyncResult> SyncDepartmentsAsync(List<Department> departments);

        /// <summary>
        /// 同步部門層級資料到目標資料庫
        /// </summary>
        Task<SyncResult> SyncDeptHierarchyAsync(List<DeptHierarchy> hierarchy);

        /// <summary>
        /// 取得資料庫名稱
        /// </summary>
        string GetDatabaseName();
    }
}
