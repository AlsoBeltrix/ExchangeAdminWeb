using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Sweep item 28. A Room Finder row's one directory session: one credential lookup, one runspace, one import, one prepared
/// credential, shared by the row's AD steps - each step's pipeline handed over clean, a runspace no longer open replaced.
/// </summary>
public sealed class ConferenceRoomDirectorySessionTests
{
    private static Runspace Open()
    {
        var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        return runspace;
    }

    [Fact]
    public async Task TheFirstLookupsAnswer_IsEveryLaterStepsAnswer_AFailureIncluded()
    {
        using var session = new ConferenceRoomDirectorySession();
        var asked = 0;

        var first = await session.LookupAsync(() => { asked++; return Task.FromResult(new ModuleCredentialLookup(null, ModuleCredentialFailure.NotReturned)); });
        var second = await session.LookupAsync(() => { asked++; return Task.FromResult(new ModuleCredentialLookup(("svc", "pw", "D"), ModuleCredentialFailure.None)); });

        Assert.Equal(1, asked);
        Assert.Equal(first, second);
        Assert.Null(second.Credentials);
        Assert.Equal(ModuleCredentialFailure.NotReturned, second.Failure);
    }

    [Fact]
    public async Task ALookupThatThrew_KeepsNothing_SoTheNextStepAsksAgain()
    {
        using var session = new ConferenceRoomDirectorySession();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.LookupAsync(() => Task.FromException<ModuleCredentialLookup>(new OperationCanceledException())));
        var answered = await session.LookupAsync(() => Task.FromResult(new ModuleCredentialLookup(("svc", "pw", "D"), ModuleCredentialFailure.None)));

        Assert.NotNull(answered.Credentials);
        Assert.Equal(2, session.Lookups);
    }

    [Fact]
    public void OneRunspaceAndOneImport_ServeEveryStep_EachHandedAClearPipeline()
    {
        using var session = new ConferenceRoomDirectorySession();
        var imports = 0;

        var ps = session.Pipeline(Open);
        session.EnsureImported(_ => imports++);
        ps.AddScript("Write-Error 'left by the first step'");
        ps.Invoke();
        ps.AddCommand("Get-Date");

        var again = session.Pipeline(Open);
        session.EnsureImported(_ => imports++);

        Assert.Same(ps, again);
        Assert.Equal(1, session.RunspacesOpened);
        Assert.Equal(1, imports);
        Assert.Empty(again.Commands.Commands);
        Assert.Empty(again.Streams.Error);
    }

    [Fact]
    public void ARunspaceNoLongerOpen_IsReplaced_AndTheModuleImportedAgain()
    {
        using var session = new ConferenceRoomDirectorySession();
        var imports = 0;
        var ps = session.Pipeline(Open);
        session.EnsureImported(_ => imports++);
        var first = ps.Runspace;

        first.Close();
        var again = session.Pipeline(Open);
        session.EnsureImported(_ => imports++);

        Assert.NotSame(first, again.Runspace);
        Assert.Equal(RunspaceState.Opened, again.Runspace.RunspaceStateInfo.State);
        Assert.Equal(2, session.RunspacesOpened);
        Assert.Equal(2, imports);
    }

    [Fact]
    public void AFailedImport_IsTriedAgainByTheNextStep()
    {
        using var session = new ConferenceRoomDirectorySession();
        session.Pipeline(Open);

        Assert.Throws<InvalidOperationException>(() => session.EnsureImported(_ => throw new InvalidOperationException("no module")));
        var imports = 0;
        session.EnsureImported(_ => imports++);

        Assert.Equal(1, imports);
    }

    [Fact]
    public void TheCredentialIsPreparedOnce()
    {
        using var session = new ConferenceRoomDirectorySession();
        var prepared = 0;
        PSCredential Create()
        {
            prepared++;
            var password = new System.Security.SecureString();
            password.AppendChar('x');
            return new PSCredential("D\\svc", password);
        }

        var first = session.Credential(Create);
        var second = session.Credential(Create);

        Assert.Same(first, second);
        Assert.Equal(1, prepared);
    }

    [Fact]
    public void DisposingTheSession_DisposesItsRunspace()
    {
        var session = new ConferenceRoomDirectorySession();
        var runspace = session.Pipeline(Open).Runspace;

        session.Dispose();

        Assert.NotEqual(RunspaceState.Opened, runspace.RunspaceStateInfo.State);
        Assert.Throws<ObjectDisposedException>(() => session.Pipeline(Open));
    }
}
