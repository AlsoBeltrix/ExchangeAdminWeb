namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// One object as a directory command returned it, reduced to the property bag the caller reads.
/// </summary>
/// <remarks>
/// A dictionary rather than the live <c>PSObject</c> deliberately. The whole reason
/// <see cref="Services.SectionAccessGroupDirectory"/> could not be tested is that every value it
/// inspected arrived wrapped in a PowerShell type; carrying that type across the seam would move
/// the untestable boundary without removing it.
///
/// Values stay <c>object?</c>, not <c>string</c>. <c>dnsRoot</c> is multi-valued in the schema and
/// arrives as a collection, and
/// <see cref="SectionAccessDirectoryReading.UnwrapDnsRoot(object?)"/> exists precisely to decide
/// what to do with that. Flattening to strings here would answer that question in the one place no
/// test can see.
/// </remarks>
public sealed class DirectoryObject
{
    private readonly IReadOnlyDictionary<string, object?> _properties;

    public DirectoryObject(IReadOnlyDictionary<string, object?> properties) => _properties = properties;

    /// <summary>
    /// The raw value of a property, or null when the object does not carry it.
    /// </summary>
    public object? Value(string property) =>
        _properties.TryGetValue(property, out var value) ? value : null;

    /// <summary>
    /// The value of a property rendered as a string, or null when absent or null.
    /// </summary>
    public string? Text(string property) => Value(property)?.ToString();
}

/// <summary>
/// What a directory command produced: the rows it emitted, and the error it reported if any.
/// </summary>
/// <remarks>
/// Rows and <see cref="Error"/> are independent, and both must be carried. A cmdlet that emits
/// rows AND reports an error has proved nothing about how many objects exist, so a caller that
/// only looked at the rows would read a partial failure as a confident answer. That is the
/// distinction <c>DrainErrors</c> existed to preserve, and it is preserved here rather than
/// collapsed into an exception.
///
/// <see cref="Rows"/> is a list of NULLABLE entries on purpose: a PowerShell pipeline can yield a
/// null element (docs/MessageTraceNullRow-Plan.md), and dropping those inside the runner would
/// make the caller's null-row guard unreachable and therefore untestable.
/// </remarks>
public sealed record DirectoryCommandOutcome(IReadOnlyList<DirectoryObject?> Rows, string? Error)
{
    public static DirectoryCommandOutcome Success(params DirectoryObject?[] rows) => new(rows, null);

    public static DirectoryCommandOutcome Failure(string error) => new([], error);
}

/// <summary>
/// The directory operations the section-access group lookup performs, separated from how they are
/// performed.
/// </summary>
/// <remarks>
/// The seam that makes <see cref="Services.SectionAccessGroupDirectory"/> testable at all. Every
/// method on it previously opened a PowerShell runspace and imported the <c>ActiveDirectory</c>
/// module, so the orchestration around those calls - which errors are fatal, which absences are
/// answers, what a partial result means - could not be reached without a domain-joined host with
/// RSAT. <see cref="SectionAccessDirectoryReading"/> extracted the pure decisions
/// (docs/CoverageRatchetRepair-Plan.md); this abstracts the I/O itself, which that plan named as
/// its own work stream.
///
/// <see cref="TranslateSidToNTAccount"/> is not a PowerShell command, and sits here anyway: it is
/// the other operation in this service that needs a live directory, and a caller faking one
/// without the other still could not run. One seam, one fake.
///
/// Implementations own a runspace, hence <see cref="IDisposable"/>.
/// </remarks>
public interface ISectionAccessDirectoryCommands : IDisposable
{
    /// <summary>
    /// Runs a directory cmdlet with the given named parameters and returns what it produced.
    /// </summary>
    /// <remarks>
    /// Does not throw for a cmdlet that reported an error - that arrives as
    /// <see cref="DirectoryCommandOutcome.Error"/>, because the caller distinguishes "the query
    /// ran and matched nothing" from "the query did not run", and only it knows which of the two a
    /// given call can tolerate. A failure to run at all (the module is missing, no domain
    /// controller answers) still throws.
    /// </remarks>
    DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters);

    /// <summary>
    /// Translates a SID to its <c>DOMAIN\Name</c> account string, or null when it cannot be
    /// translated.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: this feeds a DISPLAY string. An unreachable domain or a
    /// deleted principal must leave the operator with a bare name, never fail a lookup whose real
    /// product is the SID.
    /// </remarks>
    string? TranslateSidToNTAccount(string sid);
}
