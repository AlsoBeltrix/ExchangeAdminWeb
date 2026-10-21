using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-2. Runs the service's actual resolve and apply sequences on an opened local runspace whose
/// functions stand in for Import-Module, Get-ADUser and Set-ADUser. No AD module, directory,
/// credential use or network connection is involved.
///
/// Two SDK facts make a retained error record harmful: <c>Commands.Clear()</c> and a new
/// <c>Invoke()</c> leave <c>Streams.Error</c> intact, and <c>HadErrors</c> after an invocation is
/// true while the stream holds any record, stale ones included. A record one row left behind
/// therefore became a later row's reported error, and made a later row's clean write report Error.
/// </summary>
public sealed class LicensingUpdatesPowerShellLifecycleTests
{
    private const string License = "E5";
    private static readonly (string username, string password, string domain) Creds =
        ("svc-licensing", "not-a-secret", "EXAMPLE");

    // Row 'one' leaves a record on the stream and then fails, as a partial remote failure can.
    private const string OneFailsAfterARecord =
        "if ($Identity -eq 'CN=one') { Write-Error 'row one context' -ErrorAction Continue; throw 'row one failed' }";

    // Answers both the resolve (-LDAPFilter) and the apply re-read (-Identity) shapes.
    private const string FoundUser = """
        $name = if ($Identity) { $Identity -replace '^CN=', '' } else { $LDAPFilter -replace '^\(\|\(userPrincipalName=([^)]*)\).*$', '$1' }
        [pscustomobject]@{ DisplayName = $name; UserPrincipalName = "$name@example.test"; SamAccountName = $name; mail = "$name@example.test"; DistinguishedName = "CN=$name"; ObjectGUID = "guid-$name"; extensionAttribute11 = 'E3' }
        """;

    [Fact]
    public void ApplyChanges_LaterRowFailure_ReportsItsOwnErrorOnly()
    {
        using var session = new Session(setAdUser: OneFailsAfterARecord + "\n" +
            "if ($Identity -eq 'CN=two') { Write-Error 'row two failed' -ErrorAction Continue }");

        var results = session.Apply("one", "two");

        Assert.Collection(results,
            one => AssertRow(one, "one", "Error", "row one failed"),
            two => AssertRow(two, "two", "Error", "row two failed"));
        // Each row ran only its own commands: nothing replayed.
        Assert.Equal([
            "Import-Module|ActiveDirectory",
            "Get-ADUser|CN=one", "Set-ADUser|CN=one|E5",
            "Get-ADUser|CN=two", "Set-ADUser|CN=two|E5"
        ], session.Calls);
        session.AssertClean();
    }

    [Fact]
    public void ApplyChanges_CleanWriteAfterAFailedRow_IsReportedSuccess()
    {
        using var session = new Session(setAdUser: OneFailsAfterARecord);

        var results = session.Apply("one", "two");

        Assert.Collection(results,
            one => AssertRow(one, "one", "Error", "row one failed"),
            two =>
            {
                AssertRow(two, "two", "Success", null);
                Assert.Equal("E3", two.PreviousValue);
                Assert.Equal(License, two.NewValue);
            });
        session.AssertClean();
    }

    [Fact]
    public void ApplyChanges_ImportNoise_DoesNotFailTheFirstWrite()
    {
        using var session = new Session(importModule: "Write-Error 'import noise' -ErrorAction Continue");

        var results = session.Apply("one");

        AssertRow(Assert.Single(results), "one", "Success", null);
        session.AssertClean();
    }

    // Control: the fix must not drop a row's OWN diagnostics before its consumer reads them.
    [Fact]
    public void ApplyChanges_ARowsOwnNonTerminatingErrors_AreStillItsReportedError()
    {
        using var session = new Session(setAdUser:
            "if ($Identity -eq 'CN=one') { Write-Error 'row one first' -ErrorAction Continue; Write-Error 'row one second' -ErrorAction Continue }");

        var results = session.Apply("one", "two");

        Assert.Collection(results,
            one => AssertRow(one, "one", "Error", "row one first"),
            two => AssertRow(two, "two", "Success", null));
        session.AssertClean();
    }

    [Fact]
    public void ResolveUsers_FailedLookupThenFoundUser_LeavesNoRecordsBehind()
    {
        using var session = new Session(getAdUser:
            "if ($LDAPFilter -like '*=one)*') { Write-Error 'lookup context' -ErrorAction Continue; throw 'lookup failed' }\n" + FoundUser);

        var rows = session.Resolve("one", "two");

        Assert.Collection(rows,
            one =>
            {
                Assert.Null(one.Principal);
                Assert.Equal("lookup failed", one.Error);
            },
            two =>
            {
                Assert.Equal("CN=two", two.Principal?.DistinguishedName);
                Assert.Equal("E3", two.CurrentValue);
                Assert.Null(two.Error);
            });
        session.AssertClean();
    }

    [Fact]
    public void ResolveUsers_ImportNoise_LeavesNoRecordsBehind()
    {
        using var session = new Session(importModule: "Write-Error 'import noise' -ErrorAction Continue");

        var row = Assert.Single(session.Resolve("two"));

        Assert.Equal("CN=two", row.Principal?.DistinguishedName);
        session.AssertClean();
    }

    private static LicensePreviewRow PreviewRow(string name) => new(name,
        new ResolvedDirectoryPrincipal("OnPremAD", name, $"{name}@example.test", name,
            $"{name}@example.test", $"CN={name}", $"guid-{name}", null),
        "E3", License, null);

    private static void AssertRow(LicenseApplyRow row, string name, string status, string? error)
    {
        Assert.Equal(name, row.InputIdentity);
        Assert.Equal(status, row.Status);
        Assert.Equal(error, row.Error);
    }

    private sealed class Session : IDisposable
    {
        private readonly Runspace _runspace = RunspaceFactory.CreateRunspace();
        private readonly LicensingUpdatesService _service =
            new(null!, null!, null!, null!, null!, null!, NullLogger<LicensingUpdatesService>.Instance);

        public PowerShell Ps { get; } = PowerShell.Create();
        public List<string> Calls { get; } = new();

        // Plain (non-advanced) functions: ErrorAction binds as an ordinary parameter, so an
        // injected throw stays terminating at the SDK boundary, as in the psl-1 stand-ins.
        public Session(string importModule = "", string getAdUser = FoundUser, string setAdUser = "")
        {
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("calls", Calls);
            using (var init = PowerShell.Create())
            {
                init.Runspace = _runspace;
                init.AddScript($$"""
                    function global:Import-Module {
                        param($Name, $ErrorAction)
                        $global:calls.Add("Import-Module|$Name")
                        {{importModule}}
                    }
                    function global:Get-ADUser {
                        param($Identity, $LDAPFilter, $Properties, $Credential, $ErrorAction)
                        $global:calls.Add("Get-ADUser|$Identity$LDAPFilter")
                        {{getAdUser}}
                    }
                    function global:Set-ADUser {
                        param($Identity, $Replace, $Credential, $ErrorAction)
                        $global:calls.Add("Set-ADUser|$Identity|$($Replace['extensionAttribute11'])")
                        {{setAdUser}}
                    }
                    """);
                init.Invoke();
                Assert.Empty(init.Streams.Error);
            }
            Ps.Runspace = _runspace;
        }

        public List<LicenseApplyRow> Apply(params string[] names)
        {
            var rows = names.Select(PreviewRow).ToList();
            var protection = rows.ToDictionary(r => r.Principal!.ObjectGuid!,
                _ => ProtectedPrincipalResult.NotProtected(), StringComparer.OrdinalIgnoreCase);
            return _service.ApplyChanges(Ps, rows, License, Creds, protection, new Dictionary<string, string>());
        }

        public List<LicensePreviewRow> Resolve(params string[] identities) =>
            _service.ResolveUsers(Ps, identities.ToList(), License, Creds);

        public void AssertClean()
        {
            Assert.Empty(Ps.Streams.Error);
            Assert.Empty(Ps.Commands.Commands);
        }

        public void Dispose()
        {
            Ps.Dispose();
            _runspace.Dispose();
        }
    }
}
