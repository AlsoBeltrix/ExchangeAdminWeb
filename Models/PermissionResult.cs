namespace ExchangeAdminWeb.Models;

public class PermissionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }

    public static PermissionResult Ok(string message = "Operation completed successfully.") =>
        new() { Success = true, Message = message };

    public static PermissionResult Fail(string message, string? detail = null) =>
        new() { Success = false, Message = message, Detail = detail };
}
