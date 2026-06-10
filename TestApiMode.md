# HR API 測試模式說明

## 1. 在 Program.cs 加入測試 API 模式

在 `switch (args[0].ToLower())` 中加入新 case：

```csharp
case "--test-api":
case "-a":
    await TestHRApiAsync(appSettings, logger);
    return;
```

## 2. 加入 TestHRApiAsync 方法

在 Program.cs 中加入：

```csharp
/// <summary>
/// 測試 HR API (不連資料庫)
/// </summary>
static async Task TestHRApiAsync(AppSettings settings, ILoggerService logger)
{
    Console.WriteLine("[API 測試] 正在測試 HR API 連線...");
    Console.WriteLine();

    try
    {
        // 只建立 HR API 服務，不需要資料庫
        var hrService = new HRApiService(settings.HRApi, logger);

        // 1. 測試取得 Access Token
        Console.WriteLine("1. 測試取得 Access Token...");
        var token = await hrService.GetAccessTokenAsync();
        Console.WriteLine($"   ✓ Token 取得成功: {token.Substring(0, 20)}...");
        Console.WriteLine();

        // 設定時間範圍 (最近 30 天)
        var endTime = DateTime.Now;
        var startTime = endTime.AddDays(-30);

        // 2. 測試取得員工資料
        Console.WriteLine($"2. 測試取得員工資料 ({startTime:yyyy-MM-dd} ~ {endTime:yyyy-MM-dd})...");
        var employees = await hrService.GetEmployeesAsync(startTime, endTime);
        Console.WriteLine($"   ✓ 取得 {employees.Count} 筆員工資料");
        if (employees.Count > 0)
        {
            Console.WriteLine($"   第一筆: {employees[0].EmpNo} - {employees[0].EmpName} ({employees[0].DeptName})");
        }
        Console.WriteLine();

        // 3. 測試取得部門資料
        Console.WriteLine($"3. 測試取得部門資料 ({startTime:yyyy-MM-dd} ~ {endTime:yyyy-MM-dd})...");
        var departments = await hrService.GetDepartmentsAsync(startTime, endTime);
        Console.WriteLine($"   ✓ 取得 {departments.Count} 筆部門資料");
        if (departments.Count > 0)
        {
            Console.WriteLine($"   第一筆: {departments[0].DeptCode} - {departments[0].DeptName}");
        }
        Console.WriteLine();

        // 4. 測試取得部門層級資料
        Console.WriteLine("4. 測試取得部門層級資料...");
        var hierarchy = await hrService.GetDeptHierarchyAsync();
        Console.WriteLine($"   ✓ 取得 {hierarchy.Count} 筆部門層級資料");
        if (hierarchy.Count > 0)
        {
            Console.WriteLine($"   第一筆: {hierarchy[0].DeptCode} - Level {hierarchy[0].Level}");
        }
        Console.WriteLine();

        Console.WriteLine("[API 測試] 所有測試通過 ✓");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API 測試] 測試失敗 ✗");
        Console.WriteLine($"錯誤: {ex.Message}");
        logger.Error("API 測試失敗", ex);
    }
}
```

## 3. 執行測試

```bash
cd D:\Project\Sync104ToBpmErp
dotnet run -- --test-api
```

或編譯後執行：
```bash
dotnet build
.\bin\Debug\net8.0\Sync104ToBpmErp.exe --test-api
```

## 4. 確保 appsettings.json 有 HR API 設定

```json
{
  "HRApi": {
    "BaseUrl": "https://api.104.com.tw/hrmax/",
    "AuthEndpoint": "auth/login",
    "EmployeeEndpoint": "employee/list",
    "DepartmentEndpoint": "department/list",
    "HierarchyEndpoint": "department/hierarchy",
    "UserAccount": "your_account",
    "UserPassword": "your_password",
    "CompanyId": "your_company_id"
  }
}
```
