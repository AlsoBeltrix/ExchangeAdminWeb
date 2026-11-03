namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// The app's <c>db</c> command-line mode: <c>dotnet ExchangeAdminWeb.dll db &lt;verb&gt; ...</c>
/// (docs/ConfigDbToolInApp-Plan.md section 3.1). Replaces the ops scripts' use of sqlite3.exe.
/// Exit codes are the contract the PowerShell side trusts: 0 ok, 1 usage, 2 failure; stdout is
/// meaningful only after exit 0. docs/ProtectedPrincipalIdentifiers-Plan.md K.3 adds two verbs an administrator runs by hand
/// and no script calls - convert-identifiers and identifier-status (<see cref="ConfigDbIdentifiers"/>) - under the same
/// contract: their report is on stdout after exit 0, and on stderr, ending in an error line, after exit 2.
/// </summary>
/// <remarks>
/// Program.cs dispatches here before the bootstrap logger and before the web host builder, so this
/// mode reads no appsettings.json, writes no log file and starts nothing.
/// </remarks>
public static class ConfigDbCli
{
    public const string Command = "db";

    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitFailure = 2;

    public const string Usage =
        "Usage:\n" +
        "  ExchangeAdminWeb db backup --source <db> --dest <file>\n" +
        "  ExchangeAdminWeb db check <db>\n" +
        "  ExchangeAdminWeb db active-jobs <jobsdb>\n" +
        "  ExchangeAdminWeb db init <db>\n" +
        "  ExchangeAdminWeb db convert-identifiers <db> [--plan]\n" +
        "  ExchangeAdminWeb db identifier-status <db>\n" +
        "Exit codes: 0 ok, 1 usage, 2 failure.";

    /// <summary>
    /// False, touching nothing, unless the first argument is exactly <c>db</c>; normal hosting
    /// (ANCM passes no such argument) is untouched.
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        if (args is not { Length: > 0 } || !string.Equals(args[0], Command, StringComparison.Ordinal))
        {
            exitCode = ExitOk;
            return false;
        }

        exitCode = Run(args[1..], Console.Out, Console.Error);
        return true;
    }

    public static int Run(string[] verbArgs, TextWriter stdout, TextWriter stderr) =>
        Run(verbArgs, stdout, stderr, InvokingUserIdentifierDirectory.Open);

    /// <param name="verbArgs">The verb and its arguments.</param>
    /// <param name="stdout">The report, after exit 0.</param>
    /// <param name="stderr">Errors and usage.</param>
    /// <param name="openDirectory">
    /// The directory convert-identifiers reads, opened only when there is something to look up: in the app, Active Directory
    /// as the invoking user (<see cref="InvokingUserIdentifierDirectory"/>); in tests, a stand-in.
    /// </param>
    internal static int Run(string[] verbArgs, TextWriter stdout, TextWriter stderr, Func<IProtectedPrincipalResolveDirectory> openDirectory)
    {
        if (verbArgs.Length == 0)
            return UsageError(stderr, "no verb given");

        var verb = verbArgs[0];
        var rest = verbArgs[1..];

        switch (verb)
        {
            case "backup":
                if (!TryParseBackup(rest, out var source, out var dest))
                    return UsageError(stderr, "backup needs exactly --source <db> --dest <file>");
                return Execute(stderr, () =>
                {
                    var written = ConfigDbMaintenance.Backup(source, dest);
                    stdout.WriteLine("ok");
                    stdout.WriteLine(written);
                });

            case "check":
                if (!TrySinglePath(rest, out var checkPath))
                    return UsageError(stderr, "check needs exactly one database path");
                return Execute(stderr, () =>
                {
                    ConfigDbMaintenance.Check(checkPath);
                    stdout.WriteLine("ok");
                });

            case "active-jobs":
                if (!TrySinglePath(rest, out var jobsPath))
                    return UsageError(stderr, "active-jobs needs exactly one jobs database path");
                return Execute(stderr, () =>
                {
                    foreach (var row in ConfigDbMaintenance.ActiveJobs(jobsPath))
                        stdout.WriteLine(row);
                });

            case "init":
                if (!TrySinglePath(rest, out var initPath))
                    return UsageError(stderr, "init needs exactly one database path");
                return Execute(stderr, () => stdout.WriteLine(ConfigDbMaintenance.Init(initPath)));

            case "convert-identifiers":
                if (!TryParseConvert(rest, out var convertPath, out var plan))
                    return UsageError(stderr, "convert-identifiers needs exactly one database path, and optionally --plan");
                return Guarded(stderr, () => ConfigDbIdentifiers.Convert(convertPath, plan, openDirectory, stdout, stderr));

            case "identifier-status":
                if (!TrySinglePath(rest, out var statusPath))
                    return UsageError(stderr, "identifier-status needs exactly one database path");
                return Guarded(stderr, () => ConfigDbIdentifiers.Status(statusPath, stdout, stderr));

            default:
                return UsageError(stderr, $"unknown verb '{verb}'");
        }
    }

    private static bool TryParseBackup(string[] rest, out string source, out string dest)
    {
        source = string.Empty;
        dest = string.Empty;
        if (rest.Length != 4)
            return false;

        string? s = null;
        string? d = null;
        for (var i = 0; i < rest.Length; i += 2)
        {
            var value = rest[i + 1];
            if (string.IsNullOrWhiteSpace(value))
                return false;

            switch (rest[i])
            {
                case "--source" when s is null:
                    s = value;
                    break;
                case "--dest" when d is null:
                    d = value;
                    break;
                default:
                    return false;
            }
        }

        if (s is null || d is null)
            return false;

        source = s;
        dest = d;
        return true;
    }

    private static bool TryParseConvert(string[] rest, out string path, out bool plan)
    {
        var flags = rest.Count(a => string.Equals(a, "--plan", StringComparison.Ordinal));
        plan = flags == 1;
        path = string.Empty;
        return flags <= 1
            && TrySinglePath(rest.Where(a => !string.Equals(a, "--plan", StringComparison.Ordinal)).ToArray(), out path);
    }

    private static bool TrySinglePath(string[] rest, out string path)
    {
        path = string.Empty;
        if (rest.Length != 1 || string.IsNullOrWhiteSpace(rest[0]) || rest[0].StartsWith("--", StringComparison.Ordinal))
            return false;
        path = rest[0];
        return true;
    }

    private static int Execute(TextWriter stderr, Action action)
    {
        try
        {
            action();
            return ExitOk;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return ExitFailure;
        }
    }

    // The identifier verbs write their own report and exit code; anything they did not expect is an error line and exit 2.
    private static int Guarded(TextWriter stderr, Func<int> verb)
    {
        try
        {
            return verb();
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return ExitFailure;
        }
    }

    private static int UsageError(TextWriter stderr, string reason)
    {
        stderr.WriteLine($"error: {reason}");
        stderr.WriteLine(Usage);
        return ExitUsage;
    }
}
