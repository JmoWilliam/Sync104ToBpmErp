using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Sync104ToBpmErp.Configuration;
using Sync104ToBpmErp.Models;

namespace Sync104ToBpmErp.Services
{
    /// <summary>
    /// BPM (MS-SQL) 資料庫服務
    /// </summary>
    public class BpmDatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private readonly ILoggerService _logger;
        private readonly int _batchSize;

        public BpmDatabaseService(DatabaseSettings settings, ILoggerService logger, int batchSize = 100)
        {
            _connectionString = settings.ConnectionString;
            _logger = logger;
            _batchSize = batchSize;
        }

        /// <summary>
        /// 取得資料庫名稱
        /// </summary>
        public string GetDatabaseName() => "BPM (MS-SQL)";

        /// <summary>
        /// 建立資料庫連線
        /// </summary>
        private IDbConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        /// <summary>
        /// 測試資料庫連線
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var connection = CreateConnection();
                connection.Open();
                _logger.LogDbConnection(GetDatabaseName(), true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDbConnection(GetDatabaseName(), false, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 同步員工資料
        /// </summary>
        public async Task<SyncResult> SyncEmployeesAsync(List<Employee> employees)
        {
            var result = new SyncResult { DataType = "Employee", TargetSystem = "BPM" };

            if (employees == null || employees.Count == 0)
            {
                _logger.Warning($"[{GetDatabaseName()}] 沒有員工資料需要同步");
                return result;
            }

            using var connection = CreateConnection();
            connection.Open();

            using var transaction = connection.BeginTransaction();

            try
            {
                result.TotalCount = employees.Count;
                int processedCount = 0;

                foreach (var emp in employees)
                {
                    processedCount++;
                    try
                    {
                        // 檢查員工是否已存在
                        var existingCount = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM BPM_EMPLOYEE WHERE EMP_NO = @EmpNo",
                            new { EmpNo = emp.EmpNo },
                            transaction);

                        if (existingCount > 0)
                        {
                            // 更新現有員工
                            await connection.ExecuteAsync(@"
                                UPDATE BPM_EMPLOYEE SET
                                    EMP_NAME = @EmpName,
                                    EMP_NAME_EN = @EmpNameEn,
                                    DEPT_CODE = @DeptCode,
                                    DEPT_NAME = @DeptName,
                                    POSITION = @Position,
                                    EMAIL = @Email,
                                    PHONE = @Phone,
                                    STATUS = @Status,
                                    JOIN_DATE = @JoinDate,
                                    LEAVE_DATE = @LeaveDate,
                                    MANAGER_EMP_NO = @ManagerEmpNo,
                                    LAST_MODIFIED = @LastModified,
                                    SYNC_TIME = GETDATE()
                                WHERE EMP_NO = @EmpNo",
                                emp, transaction);

                            _logger.LogSyncDetail("Employee", "UPDATE", emp.EmpNo, true);
                        }
                        else
                        {
                            // 新增員工
                            await connection.ExecuteAsync(@"
                                INSERT INTO BPM_EMPLOYEE (
                                    EMP_NO, EMP_NAME, EMP_NAME_EN, DEPT_CODE, DEPT_NAME,
                                    POSITION, EMAIL, PHONE, STATUS, JOIN_DATE, LEAVE_DATE,
                                    MANAGER_EMP_NO, LAST_MODIFIED, SYNC_TIME
                                ) VALUES (
                                    @EmpNo, @EmpName, @EmpNameEn, @DeptCode, @DeptName,
                                    @Position, @Email, @Phone, @Status, @JoinDate, @LeaveDate,
                                    @ManagerEmpNo, @LastModified, GETDATE()
                                )",
                                emp, transaction);

                            _logger.LogSyncDetail("Employee", "INSERT", emp.EmpNo, true);
                        }

                        result.SuccessCount++;

                        // 每 100 筆記錄一次進度
                        if (processedCount % 100 == 0)
                        {
                            _logger.Info($"[{GetDatabaseName()}] 員工資料同步進度: {processedCount}/{employees.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        var errorMsg = $"員工 {emp.EmpNo} ({emp.EmpName}): {ex.Message}";
                        result.Errors.Add(errorMsg);
                        _logger.LogSyncDetail("Employee", "SYNC", emp.EmpNo, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;

                _logger.LogSyncEnd($"Employee ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);

                if (result.FailedCount > 0)
                {
                    _logger.Warning($"[{GetDatabaseName()}] 員工同步完成，但有 {result.FailedCount} 筆失敗");
                }
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步員工資料時發生嚴重錯誤，已回滾交易", ex);
                throw;
            }

            return result;
        }

        /// <summary>
        /// 同步部門資料
        /// </summary>
        public async Task<SyncResult> SyncDepartmentsAsync(List<Department> departments)
        {
            var result = new SyncResult { DataType = "Department", TargetSystem = "BPM" };

            if (departments == null || departments.Count == 0)
            {
                _logger.Warning($"[{GetDatabaseName()}] 沒有部門資料需要同步");
                return result;
            }

            using var connection = CreateConnection();
            connection.Open();

            using var transaction = connection.BeginTransaction();

            try
            {
                result.TotalCount = departments.Count;
                int processedCount = 0;

                foreach (var dept in departments)
                {
                    processedCount++;
                    int existingCount = 0;
                    try
                    {
                        // 檢查部門是否已存在
                        existingCount = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(1) FROM BPM_DEPARTMENT WHERE DEPT_CODE = @DeptCode",
                            new { DeptCode = dept.DeptCode },
                            transaction);

                        if (existingCount > 0)
                        {
                            // 更新現有部門
                            await connection.ExecuteAsync(@"
                                UPDATE BPM_DEPARTMENT SET
                                    DEPT_NAME = @DeptName,
                                    DEPT_NAME_EN = @DeptNameEn,
                                    PARENT_DEPT_CODE = @ParentDeptCode,
                                    DEPT_LEVEL = @DeptLevel,
                                    MANAGER_EMP_NO = @ManagerEmpNo,
                                    STATUS = @Status,
                                    LAST_MODIFIED = @LastModified,
                                    SYNC_TIME = GETDATE()
                                WHERE DEPT_CODE = @DeptCode",
                                dept, transaction);

                            _logger.LogSyncDetail("Department", "UPDATE", dept.DeptCode, true);
                        }
                        else
                        {
                            // 新增部門
                            await connection.ExecuteAsync(@"
                                INSERT INTO BPM_DEPARTMENT (
                                    DEPT_CODE, DEPT_NAME, DEPT_NAME_EN, PARENT_DEPT_CODE,
                                    DEPT_LEVEL, MANAGER_EMP_NO, STATUS, LAST_MODIFIED, SYNC_TIME
                                ) VALUES (
                                    @DeptCode, @DeptName, @DeptNameEn, @ParentDeptCode,
                                    @DeptLevel, @ManagerEmpNo, @Status, @LastModified, GETDATE()
                                )",
                                dept, transaction);

                            _logger.LogSyncDetail("Department", "INSERT", dept.DeptCode, true);
                        }

                        result.SuccessCount++;

                        // 每 100 筆記錄一次進度
                        if (processedCount % 100 == 0)
                        {
                            _logger.Info($"[{GetDatabaseName()}] 部門資料同步進度: {processedCount}/{departments.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        var action = existingCount > 0 ? "UPDATE" : "INSERT";
                        var errorMsg = $"部門 {dept.DeptCode} ({dept.DeptName}): {ex.Message}";
                        result.Errors.Add(errorMsg);
                        _logger.LogSyncDetail("Department", action, dept.DeptCode, false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;

                _logger.LogSyncEnd($"Department ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);

                if (result.FailedCount > 0)
                {
                    _logger.Warning($"[{GetDatabaseName()}] 部門同步完成，但有 {result.FailedCount} 筆失敗");
                }
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步部門資料時發生嚴重錯誤，已回滾交易", ex);
                throw;
            }

            return result;
        }

        /// <summary>
        /// 同步部門層級資料
        /// </summary>
        public async Task<SyncResult> SyncDeptHierarchyAsync(List<DeptHierarchy> hierarchy)
        {
            var result = new SyncResult { DataType = "DeptHierarchy", TargetSystem = "BPM" };

            if (hierarchy == null || hierarchy.Count == 0)
            {
                _logger.Warning($"[{GetDatabaseName()}] 沒有部門層級資料需要同步");
                return result;
            }

            using var connection = CreateConnection();
            connection.Open();

            using var transaction = connection.BeginTransaction();

            try
            {
                // 先清空舊的層級資料
                _logger.Info($"[{GetDatabaseName()}] 正在清空舊的部門層級資料...");
                await connection.ExecuteAsync(
                    "DELETE FROM BPM_DEPT_HIERARCHY",
                    transaction: transaction);

                result.TotalCount = hierarchy.Count;
                int processedCount = 0;

                foreach (var item in hierarchy)
                {
                    processedCount++;
                    try
                    {
                        await connection.ExecuteAsync(@"
                            INSERT INTO BPM_DEPT_HIERARCHY (
                                DEPT_CODE, PARENT_DEPT_CODE, LEVEL, PATH, SYNC_TIME
                            ) VALUES (
                                @DeptCode, @ParentDeptCode, @Level, @Path, GETDATE()
                            )",
                            item, transaction);

                        result.SuccessCount++;
                        _logger.LogSyncDetail("DeptHierarchy", "INSERT", $"{item.DeptCode}->{item.ParentDeptCode}", true);

                        // 每 100 筆記錄一次進度
                        if (processedCount % 100 == 0)
                        {
                            _logger.Info($"[{GetDatabaseName()}] 部門層級同步進度: {processedCount}/{hierarchy.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        var errorMsg = $"部門層級 {item.DeptCode} -> {item.ParentDeptCode}: {ex.Message}";
                        result.Errors.Add(errorMsg);
                        _logger.LogSyncDetail("DeptHierarchy", "INSERT", $"{item.DeptCode}->{item.ParentDeptCode}", false, ex.Message);
                    }
                }

                transaction.Commit();
                result.Success = true;

                _logger.LogSyncEnd($"DeptHierarchy ({GetDatabaseName()})", result.TotalCount, result.SuccessCount, result.FailedCount);

                if (result.FailedCount > 0)
                {
                    _logger.Warning($"[{GetDatabaseName()}] 部門層級同步完成，但有 {result.FailedCount} 筆失敗");
                }
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                result.Success = false;
                _logger.Error($"[{GetDatabaseName()}] 同步部門層級資料時發生嚴重錯誤，已回滾交易", ex);
                throw;
            }

            return result;
        }
    }
}
