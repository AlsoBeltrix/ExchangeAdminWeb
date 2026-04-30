using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public interface IExchangeService
{
    Task<PermissionResult> AddMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs, bool autoMapping);
    Task<PermissionResult> RemoveMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs);
    Task<PermissionResult> SetCalendarPermissionAsync(string targetMailbox, string user, CalendarAccessRight accessRight);
    Task<PermissionResult> RemoveCalendarPermissionAsync(string targetMailbox, string user);
    Task<BulkOperationResult> ProcessMailboxPermissionsCsvAsync(Stream csvStream, bool isAdd, PermissionValidator validator, string currentUser);
    Task<BulkOperationResult> ProcessCalendarPermissionsCsvAsync(Stream csvStream, bool isSet, PermissionValidator validator, string currentUser);
}
