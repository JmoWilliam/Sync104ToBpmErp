using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// HR API 服務實作
    /// </summary>
    public class HRApiService : IHRApiService
    {
        private readonly HttpClient _httpClient;
        private readonly HRApiSettings _settings;
        private readonly ILoggerService _logger;
        private string? _accessToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

        public HRApiService(HRApiSettings settings, ILoggerService logger)
        {
            _settings = settings;
            _logger = logger;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(settings.BaseUrl),
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        /// <summary>
        /// 取得 Access Token
        /// </summary>
        public async Task<string> GetAccessTokenAsync()
        {
            // 檢查 Token 是否仍有效（提前5分鐘過期）
            if (_accessToken != null && DateTime.Now < _tokenExpiry.AddMinutes(-5))
            {
                return _accessToken;
            }

            try
            {
                _logger.Info("[HR API] 正在取得 Access Token...");

                // 使用 USER_ACCOUNT 和 USER_PWD 取得 Token
                var requestBody = new
                {
                    USER_ACCOUNT = _settings.UserAccount,
                    USER_PWD = _settings.UserPassword
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(_settings.AuthEndpoint, content);
                response.EnsureSuccessStatusCode();

                var tokenResponse = await response.Content.ReadFromJsonAsync<ApiTokenResponse>();

                // 檢查回應碼和資料
                if (tokenResponse == null || tokenResponse.Code != 200 || tokenResponse.Data == null)
                {
                    throw new Exception($"無法從 HR API 取得有效的 Access Token，回應碼: {tokenResponse?.Code}");
                }

                _accessToken = tokenResponse.Data.AccessToken;
                // 從 JWT token 解析過期時間，或使用預設值 30 分鐘
                _tokenExpiry = DateTime.Now.AddMinutes(30);

                _logger.Info($"[HR API] 成功取得 Access Token (USER_ID: {tokenResponse.Data.UserId})");

                return _accessToken;
            }
            catch (Exception ex)
            {
                _logger.Error("[HR API] 取得 Access Token 失敗", ex);
                throw;
            }
        }

        /// <summary>
        /// 設定 HTTP 請求的 Authorization Header
        /// </summary>
        private async Task SetAuthorizationHeaderAsync()
        {
            var token = await GetAccessTokenAsync();
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        /// <summary>
        /// 取得所有公司資訊 (/api/os/company)
        /// </summary>
        public async Task<List<CompanyInfo>> GetCompaniesAsync()
        {
            try
            {
                await SetAuthorizationHeaderAsync();

                _logger.Info($"[HR API] 正在呼叫公司資料 API: {_settings.CompanyEndpoint}");

                var requestBody = new
                {
                    ACCESS_TOKEN = _accessToken
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(_settings.CompanyEndpoint, content);
                response.EnsureSuccessStatusCode();

                var jsonString = await response.Content.ReadAsStringAsync();
                _logger.Info($"[HR API] 公司 API 回應: {jsonString.Substring(0, Math.Min(200, jsonString.Length))}...");

                // 嘗試解析為包裝回應格式
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<List<CompanyInfo>>>(jsonString);

                if (apiResponse?.IsSuccess == true && apiResponse.Data != null)
                {
                    var count = apiResponse.Data.Count;
                    _logger.Info($"[HR API] 成功取得 {count} 筆公司資料");
                    return apiResponse.Data;
                }

                // 如果包裝格式解析失敗，嘗試直接解析為 List<CompanyInfo>
                var companies = JsonSerializer.Deserialize<List<CompanyInfo>>(jsonString);
                if (companies != null)
                {
                    _logger.Info($"[HR API] 成功取得 {companies.Count} 筆公司資料 (直接格式)");
                    return companies;
                }

                _logger.Warning($"[HR API] 無法解析公司資料回應，Code: {apiResponse?.Code}, Message: {apiResponse?.Message}");
                return new List<CompanyInfo>();
            }
            catch (Exception ex)
            {
                _logger.Error("[HR API] 取得公司資料失敗", ex);
                throw;
            }
        }

        /// <summary>
        /// 取得所屬 CO_ID（優先使用傳入參數，否則回退設定值）
        /// </summary>
        private long ResolveCompanyId(long? companyId)
        {
            return companyId ?? _settings.CompanyId;
        }

        /// <summary>
        /// 取得所有員工資料 (依時間範圍)
        /// </summary>
        public async Task<List<Employee>> GetEmployeesAsync(DateTime startTime, DateTime endTime, long? companyId = null)
        {
            try
            {
                await SetAuthorizationHeaderAsync();

                var coId = ResolveCompanyId(companyId);

                // 格式化時間參數 (yyyy-MM-dd HH:mm:ss 格式)
                var startTimeStr = startTime.ToString("yyyy-MM-dd HH:mm:ss");
                var endTimeStr = endTime.ToString("yyyy-MM-dd HH:mm:ss");

                // 建立 POST 請求內容，符合 104 HR Max API 規格
                var requestBody = new
                {
                    ACCESS_TOKEN = _accessToken,
                    CO_ID = coId,
                    C_SDATETIME = startTimeStr,
                    C_EDATETIME = endTimeStr
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json");

                _logger.Info($"[HR API] 正在呼叫員工資料 API: {_settings.EmployeeEndpoint} (CO_ID={coId})");
                _logger.Info($"[HR API] 查詢時間範圍: {startTime:yyyy-MM-dd HH:mm:ss} ~ {endTime:yyyy-MM-dd HH:mm:ss}");

                var response = await _httpClient.PostAsync(_settings.EmployeeEndpoint, content);
                response.EnsureSuccessStatusCode();

                // 先讀取原始 JSON 內容
                var jsonString = await response.Content.ReadAsStringAsync();
                _logger.Info($"[HR API] 員工 API 回應: {jsonString.Substring(0, Math.Min(200, jsonString.Length))}...");

                // 嘗試解析為包裝回應格式
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<List<Employee>>>(jsonString);

                if (apiResponse?.IsSuccess == true && apiResponse.Data != null)
                {
                    var count = apiResponse.Data.Count;
                    _logger.Info($"[HR API] 成功取得 {count} 筆員工資料 (CO_ID={coId})");
                    return apiResponse.Data;
                }

                // 如果包裝格式解析失敗，嘗試直接解析為 List<Employee>
                var employees = JsonSerializer.Deserialize<List<Employee>>(jsonString);
                if (employees != null)
                {
                    _logger.Info($"[HR API] 成功取得 {employees.Count} 筆員工資料 (直接格式, CO_ID={coId})");
                    return employees;
                }

                _logger.Warning($"[HR API] 無法解析員工資料回應，Code: {apiResponse?.Code}, Message: {apiResponse?.Message}");
                return new List<Employee>();
            }
            catch (Exception ex)
            {
                _logger.Error("[HR API] 取得員工資料失敗", ex);
                throw;
            }
        }

        /// <summary>
        /// 取得所有部門資料 (依時間範圍)
        /// </summary>
        public async Task<List<Department>> GetDepartmentsAsync(DateTime startTime, DateTime endTime, long? companyId = null)
        {
            try
            {
                await SetAuthorizationHeaderAsync();

                var coId = ResolveCompanyId(companyId);

                // 格式化時間參數 (yyyy-MM-dd HH:mm:ss 格式)
                var startTimeStr = startTime.ToString("yyyy-MM-dd HH:mm:ss");
                var endTimeStr = endTime.ToString("yyyy-MM-dd HH:mm:ss");

                // 建立 POST 請求內容，符合 104 HR Max API 規格
                // 參考文件: /api/os/dept 需要 ORG_TYPE_CODE 和 BASE_DATE
                var requestBody = new
                {
                    ACCESS_TOKEN = _accessToken,
                    CO_ID = coId,
                    ORG_TYPE_CODE = _settings.OrgTypeCode,
                    BASE_DATE = _settings.BaseDate,
                    E_SDATETIME = startTimeStr,
                    E_EDATETIME = endTimeStr
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json");

                _logger.Info($"[HR API] 正在呼叫部門資料 API: {_settings.DepartmentEndpoint} (CO_ID={coId})");
                _logger.Info($"[HR API] 查詢時間範圍: {startTime:yyyy-MM-dd HH:mm:ss} ~ {endTime:yyyy-MM-dd HH:mm:ss}");

                var response = await _httpClient.PostAsync(_settings.DepartmentEndpoint, content);
                response.EnsureSuccessStatusCode();

                // 先讀取原始 JSON 內容
                var jsonString = await response.Content.ReadAsStringAsync();
                _logger.Info($"[HR API] 部門 API 回應: {jsonString.Substring(0, Math.Min(200, jsonString.Length))}...");

                // 嘗試解析為包裝回應格式
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<List<Department>>>(jsonString);

                if (apiResponse?.IsSuccess == true && apiResponse.Data != null)
                {
                    var count = apiResponse.Data.Count;
                    _logger.Info($"[HR API] 成功取得 {count} 筆部門資料 (CO_ID={coId})");
                    return apiResponse.Data;
                }

                // 如果包裝格式解析失敗，嘗試直接解析為 List<Department>
                var departments = JsonSerializer.Deserialize<List<Department>>(jsonString);
                if (departments != null)
                {
                    _logger.Info($"[HR API] 成功取得 {departments.Count} 筆部門資料 (直接格式, CO_ID={coId})");
                    return departments;
                }

                _logger.Warning($"[HR API] 無法解析部門資料回應，Code: {apiResponse?.Code}, Message: {apiResponse?.Message}");
                return new List<Department>();
            }
            catch (Exception ex)
            {
                _logger.Error("[HR API] 取得部門資料失敗", ex);
                throw;
            }
        }

        /// <summary>
        /// 取得部門層級資料
        /// </summary>
        public async Task<List<DeptHierarchy>> GetDeptHierarchyAsync(long? companyId = null)
        {
            try
            {
                await SetAuthorizationHeaderAsync();

                var coId = ResolveCompanyId(companyId);

                _logger.Info($"[HR API] 正在呼叫部門層級資料 API: {_settings.HierarchyEndpoint} (CO_ID={coId})");

                // 建立 POST 請求內容，符合 104 HR Max API 規格
                var requestBody = new
                {
                    ACCESS_TOKEN = _accessToken,
                    CO_ID = coId,
                    ORG_TYPE_CODE = _settings.OrgTypeCode,
                    BASE_DATE = _settings.BaseDate
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(_settings.HierarchyEndpoint, content);
                response.EnsureSuccessStatusCode();

                // 先讀取原始 JSON 內容
                var jsonString = await response.Content.ReadAsStringAsync();
                _logger.Info($"[HR API] 部門層級 API 回應: {jsonString.Substring(0, Math.Min(200, jsonString.Length))}...");

                // 嘗試解析為包裝回應格式
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<List<DeptHierarchy>>>(jsonString);

                if (apiResponse?.IsSuccess == true && apiResponse.Data != null)
                {
                    var count = apiResponse.Data.Count;
                    _logger.Info($"[HR API] 成功取得 {count} 筆部門層級資料 (CO_ID={coId})");
                    return apiResponse.Data;
                }

                // 如果包裝格式解析失敗，嘗試直接解析為 List<DeptHierarchy>
                var hierarchy = JsonSerializer.Deserialize<List<DeptHierarchy>>(jsonString);
                if (hierarchy != null)
                {
                    _logger.Info($"[HR API] 成功取得 {hierarchy.Count} 筆部門層級資料 (直接格式, CO_ID={coId})");
                    return hierarchy;
                }

                _logger.Warning($"[HR API] 無法解析部門層級資料回應，Code: {apiResponse?.Code}, Message: {apiResponse?.Message}");
                return new List<DeptHierarchy>();
            }
            catch (Exception ex)
            {
                _logger.Error("[HR API] 取得部門層級資料失敗", ex);
                throw;
            }
        }
    }
}
