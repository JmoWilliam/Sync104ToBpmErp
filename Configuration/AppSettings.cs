namespace Sync104ToBpmErp.Configuration
{
    /// <summary>
    /// HR API 設定
    /// </summary>
    public class HRApiSettings
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string AuthEndpoint { get; set; } = string.Empty;
        public string CompanyEndpoint { get; set; } = "/api/os/company";
        public string EmployeeEndpoint { get; set; } = string.Empty;
        public string DepartmentEndpoint { get; set; } = string.Empty;
        public string HierarchyEndpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;

        // 104 HR Max API 認證資訊
        public string UserAccount { get; set; } = string.Empty;
        public string UserPassword { get; set; } = string.Empty;

        // 預設公司 ID（作為 fallback，可從 /api/os/company 動態取得）
        public long CompanyId { get; set; }

        // 組織類別代碼 (預設 1 = 部門)
        public string OrgTypeCode { get; set; } = "1";

        // 基準日 (預設今天)
        public string BaseDate { get; set; } = DateTime.Now.ToString("yyyy/MM/dd");
    }

    /// <summary>
    /// 資料庫連線設定
    /// </summary>
    public class DatabaseSettings
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
    }

    /// <summary>
    /// 同步設定
    /// </summary>
    public class SyncSettings
    {
        public int BatchSize { get; set; } = 100;
        public string LogDirectory { get; set; } = "Logs";
        public int SyncIntervalMinutes { get; set; } = 60;
    }

    /// <summary>
    /// 應用程式設定總覽
    /// </summary>
    public class AppSettings
    {
        public HRApiSettings HRApi { get; set; } = new HRApiSettings();
        public DatabaseSettings BpmDatabase { get; set; } = new DatabaseSettings();
        public DatabaseSettings ErpDatabase { get; set; } = new DatabaseSettings();
        public SyncSettings SyncSettings { get; set; } = new SyncSettings();
    }
}
