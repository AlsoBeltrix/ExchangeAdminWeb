using System.Management.Automation;
using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The Exchange Online reads the excluded-user group expansion performs, separated from how they
/// are performed.
/// </summary>
/// <remarks>
/// The seam that makes <see cref="PermissionValidator"/>'s group expansion testable. Every path
/// through it ran cmdlets on a borrowed EXO runspace, so the decisions around them - which errors
/// are fatal, which absences keep the entry as a literal match, and which member attributes make a
/// person protected - could not be reached without a live Exchange connection.
///
/// Reuses <see cref="DirectoryCommandOutcome"/> rather than declaring a parallel EXO type: it
/// carries rows and an error as independent values, which is the property that matters here too,
/// and a second identical shape would invite the two drifting apart.
///
/// Not <see cref="IDisposable"/>, deliberately, and the difference from
/// <see cref="ISectionAccessDirectoryCommands"/> is the point: this one runs on a runspace BORROWED
/// from <see cref="ExoConnectionPool"/>, which owns its lifetime and decides whether it returns to
/// the pool or is discarded. An implementation that disposed it would destroy a pooled connection
/// mid-flight.
/// </remarks>
internal interface IExoRecipientCommands
{
    /// <summary>
    /// Runs an EXO cmdlet with the given named parameters and returns what it produced.
    /// </summary>
    /// <remarks>
    /// Reports a cmdlet error as <see cref="DirectoryCommandOutcome.Error"/> rather than throwing,
    /// because the caller turns only SOME errors into failures - an EXO "couldn't be found" keeps
    /// the excluded entry as a literal match instead. A failure to run at all still throws, and
    /// the caller must let that propagate: <see cref="ExoConnectionPool"/> classifies the pooled
    /// session as dead or retriable from the exception, so swallowing it here would leave a dead
    /// connection in the pool.
    /// </remarks>
    DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters);
}

/// <summary>
/// Runs EXO cmdlets on a runspace borrowed from <see cref="ExoConnectionPool"/>.
/// </summary>
/// <remarks>
/// Owns nothing and decides nothing: it invokes, reports, and leaves the borrowed connection to
/// the pool. The pipeline is cleared after every call, including a failed one - a borrow that
/// returns with commands still attached poisons whoever borrows it next.
/// </remarks>
internal sealed class PooledExoCommands : IExoRecipientCommands
{
    private readonly PowerShell _ps;

    public PooledExoCommands(PowerShell ps) => _ps = ps;

    public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
    {
        try
        {
            var cmd = _ps.AddCommand(command);
            foreach (var (name, value) in parameters)
                cmd.AddParameter(name, value);

            var results = _ps.Invoke();

            // Read the error stream before clearing it: rows and an error are independent, and the
            // caller needs both to tell a partial failure from an empty answer.
            var error = _ps.HadErrors
                ? _ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Unknown EXO error"
                : null;

            if (error is not null)
                _ps.Streams.Error.Clear();

            // Null rows are preserved, not filtered: a pipeline can yield a null element
            // (docs/MessageTraceNullRow-Plan.md) and the caller owns that guard.
            return new DirectoryCommandOutcome(results.Select(ToDirectoryObject).ToList(), error);
        }
        finally
        {
            _ps.Commands.Clear();
        }
    }

    private static DirectoryObject? ToDirectoryObject(PSObject? source)
    {
        if (source is null)
            return null;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.Properties)
        {
            // A property whose getter throws must not fail the whole row: the caller decides what
            // a missing attribute means, and for most of them the answer is to skip that attribute.
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
}
