using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public interface IExchangeService
{
    Task<PermissionResult> AddMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs, bool autoMapping);
    Task<PermissionResult> RemoveMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs);
    Task<PermissionResult> SetCalendarPermissionAsync(string targetMailbox, string user, CalendarAccessRight accessRight);
    Task<PermissionResult> RemoveCalendarPermissionAsync(string targetMailbox, string user);
    Task<BulkOperationResult> ProcessMailboxPermissionsCsvAsync(Stream csvStream, bool isAdd, PermissionValidator validator, string currentUser, AuditService audit, string ipAddress, string ticketNumber);
    Task<BulkOperationResult> ProcessCalendarPermissionsCsvAsync(Stream csvStream, bool isSet, PermissionValidator validator, string currentUser, AuditService audit, string ipAddress, string ticketNumber);
    Task<MigrationEligibilityResult> CheckMigrationEligibilityAsync(string emailAddress, MigrationDirection direction);
    Task<MigrationBatchResult> CheckBulkMigrationEligibilityAsync(Stream csvStream, MigrationDirection direction);
    Task<PermissionResult> CreateMigrationBatchAsync(MigrationDirection direction, List<string> eligibleEmails, string batchName, bool autoStart, bool autoComplete);
    Task<List<MigrationBatchInfo>> GetMigrationBatchesAsync();
    Task<List<MigrationUserInfo>> GetMigrationBatchUsersAsync(string batchName);
    Task<PermissionResult> CompleteMigrationBatchAsync(string batchName);
    Task<PermissionResult> CompleteMigrationUserAsync(string emailAddress);
    Task<PermissionResult> ApproveMigrationUserAsync(string emailAddress);
    Task<PermissionResult> StopMigrationUserAsync(string emailAddress);
    Task<PermissionResult> ResumeMigrationUserAsync(string emailAddress);
    Task<PermissionResult> RemoveMigrationUserAsync(string emailAddress);
    Task<PermissionResult> RemoveMigrationBatchAsync(string batchName);
    Task<string?> GetMigrationUserReportAsync(string emailAddress);
}
