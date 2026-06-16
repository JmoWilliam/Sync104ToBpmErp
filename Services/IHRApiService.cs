using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// HR API 服務介面
    /// </summary>
    public interface IHRApiService : IDisposable
    {
        /// <summary>
        /// 取得 Access Token
        /// </summary>
        Task<string> GetAccessTokenAsync();

        /// <summary>
        /// 取得所有公司資訊 (/api/os/company)
        /// </summary>
        Task<List<CompanyInfo>> GetCompaniesAsync();

        /// <summary>
        /// 取得所有員工資料 (依時間範圍)
        /// </summary>
        /// <param name="startTime">開始時間</param>
        /// <param name="endTime">截止時間</param>
        /// <param name="companyId">公司 CO_ID (null 時使用 CompanyId 設定值)</param>
        Task<List<Employee>> GetEmployeesAsync(DateTime startTime, DateTime endTime, long? companyId = null);

        /// <summary>
        /// 取得所有部門資料 (依時間範圍)
        /// </summary>
        /// <param name="startTime">開始時間</param>
        /// <param name="endTime">截止時間</param>
        /// <param name="companyId">公司 CO_ID (null 時使用 CompanyId 設定值)</param>
        Task<List<Department>> GetDepartmentsAsync(DateTime startTime, DateTime endTime, long? companyId = null);

        /// <summary>
        /// 取得部門層級資料
        /// </summary>
        /// <param name="companyId">公司 CO_ID (null 時使用 CompanyId 設定值)</param>
        Task<List<DeptHierarchy>> GetDeptHierarchyAsync(long? companyId = null);
    }
}
