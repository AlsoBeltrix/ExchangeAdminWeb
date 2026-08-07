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

    /// <summary>
    /// Set when the target was a protected principal and an authorised servicer overrode
    /// the refusal. Names the authorising group and the rules overridden.
    /// </summary>
    /// <remarks>
    /// Carried on the result because some modules check protection in the service but audit
    /// in the page (the duplicate page gate was removed as it produced unaudited attempts).
    /// The note must reach that audit call, and it belongs in the event's <c>extra</c> - never
    /// <c>errorDetail</c>, which is discarded on a successful action.
    /// </remarks>
    public string? ServicedNote { get; init; }

    public static PermissionResult Ok(string message = "Operation completed successfully.", string? servicedNote = null) =>
        new() { Success = true, Message = message, ServicedNote = servicedNote };

    public static PermissionResult Fail(string message, string? detail = null) =>
        new() { Success = false, Message = message, Detail = detail };
}
