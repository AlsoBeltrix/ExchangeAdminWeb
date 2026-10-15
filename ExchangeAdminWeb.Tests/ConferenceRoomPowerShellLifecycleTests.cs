using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Runs the service's actual permission sequence against harmless local SDK functions.
/// The first add records its simulated effect before throwing once, as a remote mutation can.
/// No Exchange/AD module, credential or network connection is used.
/// </summary>
public sealed class ConferenceRoomPowerShellLifecycleTests : IDisposable
{
    private const string Folder = "room@example.test:\\Calendar";
    private const string Admin = "calendar-admins@example.test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-room-ps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CalendarPermissionSequence_AFailedAddIsNotReplayed()
    {
        var (result, calls, effects) = RunPermissionSequence();

        Assert.Equal(new[] { "Anonymous", "Default", Admin }, effects);
        Assert.Single(calls, call => call == $"Add|{Folder}|Anonymous|Reviewer");
        AssertOriginalFailureRemainsReported(result);
    }

    [Fact]
    public void CalendarPermissionSequence_LaterUsersReceiveOnlyTheirOwnCommands()
    {
        var (result, calls, _) = RunPermissionSequence();

        Assert.Equal(new[]
        {
            $"Add|{Folder}|Anonymous|Reviewer",
            $"Add|{Folder}|Default|Reviewer",
            $"Set|{Folder}|Default|Reviewer",
            $"Add|{Folder}|{Admin}|Editor",
            $"Set|{Folder}|{Admin}|Editor"
        }, calls);
        AssertOriginalFailureRemainsReported(result);
    }

    private static void AssertOriginalFailureRemainsReported(RoomOperationResult result)
    {
        Assert.Collection(result.Steps,
            first =>
            {
                Assert.Equal("Permission: Anonymous -> Reviewer", first.Stage);
                Assert.False(first.Success);
                Assert.Contains("first add failed after its effect", first.Error, StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal("Permission: Default -> Reviewer", second.Stage);
                Assert.True(second.Success);
                Assert.Null(second.Error);
            },
            third =>
            {
                Assert.Equal($"Permission: {Admin} -> Editor", third.Stage);
                Assert.True(third.Success);
                Assert.Null(third.Error);
            });
    }

    private (RoomOperationResult result, List<string> calls, List<string> effects) RunPermissionSequence()
    {
        Directory.CreateDirectory(_root);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_root);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(_root), NullLogger<ModuleConfigService>.Instance);
        moduleConfig.SaveModuleConfig("ConferenceRooms", new() { ["ConfAdminsGroup"] = Admin });
        var service = new ConferenceRoomService(null!, null!, moduleConfig, null!, null!,
            new ConfigurationBuilder().Build(), NullLogger<ConferenceRoomService>.Instance);

        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var calls = new List<string>();
        var effects = new List<string>();
        runspace.SessionStateProxy.SetVariable("calls", calls);
        runspace.SessionStateProxy.SetVariable("effects", effects);
        // Plain ErrorAction parameters keep this injected throw terminating at the SDK boundary;
        // an advanced function would let SilentlyContinue consume it as a cmdlet error instead.
        ps.AddScript("""
            $global:failedOnce = $false
            function global:Add-MailboxFolderPermission {
                param($Identity, $AccessRights, $User, $ErrorAction)
                $global:calls.Add("Add|$Identity|$User|$AccessRights")
                $global:effects.Add($User)
                if (-not $global:failedOnce) {
                    $global:failedOnce = $true
                    throw 'first add failed after its effect'
                }
            }
            function global:Set-MailboxFolderPermission {
                param($Identity, $AccessRights, $User, $ErrorAction)
                $global:calls.Add("Set|$Identity|$User|$AccessRights")
            }
            """);
        ps.Invoke();
        Assert.Empty(ps.Streams.Error);
        ps.Commands.Clear();

        var result = new RoomOperationResult();
        // Reflection reaches the existing switch and caught-failure continuation; no modeled loop.
        typeof(ConferenceRoomService).GetMethod("SetCalendarPermissionsForType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [ps, new ExchangeServiceBase.ConnectionErrorTracker(), Folder,
                RoomType.Exception, "", "arbiter@example.test", result]);

        Assert.Empty(ps.Commands.Commands);
        Assert.Empty(ps.Streams.Error);
        return (result, calls, effects);
    }
}
