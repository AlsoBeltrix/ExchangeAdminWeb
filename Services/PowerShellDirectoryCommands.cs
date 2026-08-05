using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Runs the section-access directory commands against live Active Directory through PowerShell.
/// </summary>
/// <remarks>
/// The half of <see cref="SectionAccessGroupDirectory"/> that genuinely needs a domain-joined host
/// with RSAT, separated so the orchestration around it does not. Nothing here decides anything: it
/// opens a runspace, invokes what it is told, and reports what came back. Every judgement about
/// what a result MEANS lives on the other side of
/// <see cref="ISectionAccessDirectoryCommands"/> - which is what makes that judgement testable.
///
/// Its own runspace, not a shared one: a startup-time migration must not queue behind a 30-second
/// lock held by interactive autocomplete keystrokes.
///
/// Runs under the app pool's ambient identity - a read-only group lookup does not need the
/// protected-principal directory-read secret (.agents/decisions.md 2026-07-31).
/// </remarks>
public sealed class PowerShellDirectoryCommands : ISectionAccessDirectoryCommands
{
    private readonly Lazy<Runspace> _runspace;

    public PowerShellDirectoryCommands() => _runspace = new Lazy<Runspace>(CreateRunspace);

    /// <inheritdoc />
    public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace.Value;

        var cmd = ps.AddCommand(command);
        foreach (var (name, value) in parameters)
            cmd.AddParameter(name, value);

        var results = ps.Invoke();

        // Read the error stream BEFORE clearing anything: rows and an error are independent, and
        // the caller needs both to tell a partial failure from an empty answer.
        var error = ps.HadErrors
            ? ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "the directory reported an error"
            : null;

        ps.Streams.Error.Clear();
        ps.Commands.Clear();

        // Null rows are preserved, not filtered: a pipeline can yield a null element
        // (docs/MessageTraceNullRow-Plan.md) and the caller owns that guard.
        var rows = results.Select(ToDirectoryObject).ToList();

        return new DirectoryCommandOutcome(rows, error);
    }

    /// <inheritdoc />
    public string? TranslateSidToNTAccount(string sid)
    {
        try
        {
            return new System.Security.Principal.SecurityIdentifier(sid)
                .Translate(typeof(System.Security.Principal.NTAccount))
                .Value;
        }
        catch
        {
            // Fail-soft by contract: this decorates a display string. The caller logs.
            return null;
        }
    }

    public void Dispose()
    {
        if (_runspace.IsValueCreated)
            _runspace.Value.Dispose();
    }

    private static DirectoryObject? ToDirectoryObject(PSObject? source)
    {
        if (source is null)
            return null;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.Properties)
        {
            // A property whose getter throws (an unreadable attribute) must not fail the whole
            // row: the caller decides what a missing attribute means, and for most of them the
            // answer is a fallback rather than an error.
            try
            {
                properties[property.Name] = property.Value;
            }
            catch
            {
                properties[property.Name] = null;
            }
        }

        return new DirectoryObject(properties);
    }

    private static Runspace CreateRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ImportPSModule("ActiveDirectory");

        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        return runspace;
    }
}
