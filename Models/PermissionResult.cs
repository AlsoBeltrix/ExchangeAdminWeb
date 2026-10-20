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
    /// The rights that were actually applied, whether or not the operation succeeded overall.
    /// Null or empty when nothing landed.
    /// </summary>
    /// <remarks>
    /// Audit finding A23. A partial grant - FullAccess applied, SendAs failed - returns
    /// <see cref="Success"/> false, and the page gated the affected-user notification on
    /// Success, so the person whose access had really changed was told nothing.
    ///
    /// This is a field rather than a parse of <see cref="Detail"/> because Detail is
    /// overloaded: on a partial result it holds the applied rights, but on a successful cloud
    /// grant it holds the OWA link. Reading rights out of it would put a URL in the
    /// permission-type line of a user's mail.
    /// </remarks>
    public IReadOnlyList<string>? AppliedRights { get; init; }

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

    /// <summary>
    /// The same result carrying a serviced note (pgwt-7). After a servicer override EVERY
    /// outcome - success or failure - must reach the audit with the note, or a failed write on
    /// a protected target loses the record of who was authorised to attempt it (and a write
    /// that timed out AFTER committing at the DC audits as a noteless failure). No-op when the
    /// note is blank or unchanged.
    /// </summary>
    public PermissionResult WithServicedNote(string? servicedNote)
        => string.IsNullOrWhiteSpace(servicedNote) || ServicedNote == servicedNote
            ? this
            : new PermissionResult
            {
                Success = Success,
                Message = Message,
                Detail = Detail,
                ExcludedTargets = ExcludedTargets,
                AppliedRights = AppliedRights,
                ServicedNote = servicedNote,
            };
}
