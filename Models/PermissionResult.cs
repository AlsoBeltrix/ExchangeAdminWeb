namespace ExchangeAdminWeb.Models;

public class PermissionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }

    /// <summary>
    /// Targets that were deliberately excluded from an otherwise-successful operation
    /// (e.g. protected principals filtered out of a migration batch). Each entry is a
    /// human-readable "identity - reason" string. Null/empty when nothing was excluded.
    /// The UI must surface this prominently - an exclusion is never silent.
    /// </summary>
    public IReadOnlyList<string>? ExcludedTargets { get; init; }

    public static PermissionResult Ok(string message = "Operation completed successfully.") =>
        new() { Success = true, Message = message };

    public static PermissionResult Fail(string message, string? detail = null) =>
        new() { Success = false, Message = message, Detail = detail };
}
