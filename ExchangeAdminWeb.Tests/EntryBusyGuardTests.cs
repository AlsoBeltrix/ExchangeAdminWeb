using System.Reflection;
using System.Runtime.CompilerServices;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Sweep item 42. Write handlers that are their own click target had no entry busy guard - kd-72 and kd-74 fixed five pages -
/// so a second click the browser sent before the disabled button rendered (one round trip stale) ran the write again: a
/// second bulk CSV with its mails, a second Conference Rooms job, a second M365 group or Conditional Access named location,
/// a second DHCP authorization, a second MFA reset, a second job cancel or removal. Each now returns at once, as its first
/// statement, while its own flag is up. On Intune Devices and Risky Users the stale second Confirm threw from its lambda
/// (confirmAction.Value, cleared by the first) and ended the circuit; it is now a no-op.
/// </summary>
/// <remarks>
/// Each handler is driven on a bare page - no injected service set - with its busy flag raised, as the first click leaves it.
/// A handler that returns at the guard completes synchronously and cleanly; one without it goes on to its own first await
/// (each yields before its first service call) or throws on a missing service, so it either does not complete or faults.
/// The flag is left as the first click set it: the guard lowers nothing.
/// </remarks>
public sealed class EntryBusyGuardTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static TheoryData<Type, string, string> Handlers => new()
    {
        { typeof(MailboxPermissions), "SubmitSingle", "isLoading" },
        { typeof(MailboxPermissions), "ProcessBulk", "isLoading" },
        { typeof(CalendarPermissions), "SubmitSingle", "isLoading" },
        { typeof(CalendarPermissions), "ProcessBulk", "isLoading" },
        { typeof(ConferenceRooms), "ApplyFinderCsv", "isCsvProcessing" },
        { typeof(ConferenceRooms), "ApplyTypeCsv", "isCsvProcessing" },
        { typeof(M365GroupManagement), "SaveGroup", "isOperating" },
        { typeof(NamedLocations), "SaveLocation", "isOperating" },
        { typeof(DhcpAuthorization), "AuthorizeServer", "isOperating" },
        { typeof(DhcpAuthorization), "RemoveServer", "isOperating" },
        { typeof(GroupManagement), "AddMember", "isLoading" },
        { typeof(GroupManagement), "RemoveMember", "isLoading" },
        { typeof(MfaReset), "ExecuteReset", "isLoading" },
        { typeof(MfaReset), "ExecuteReset", "resetInProgress" }, // rev-d2: up until the notification has been sent
        { typeof(AdminBulkJobs), "CancelJob", "isActing" },
        { typeof(AdminBulkJobs), "RemoveJob", "isActing" },
    };

    [Theory]
    [MemberData(nameof(Handlers))]
    public void ASecondClickWhileTheWriteRuns_ReturnsAtOnce_AndLeavesTheFlagUp(Type page, string handler, string flag)
    {
        var instance = Activator.CreateInstance(page)!;
        page.GetField(flag, PrivateInstance)!.SetValue(instance, true);
        PrepareFormFor(instance, handler);

        var second = Invoke(instance, handler);

        Assert.True(second.IsCompletedSuccessfully,
            $"{page.Name}.{handler} ran on while {flag} was up: {(second.IsFaulted ? second.Exception!.InnerException!.Message : "it did not return at once")}");
        Assert.True((bool)page.GetField(flag, PrivateInstance)!.GetValue(instance)!, $"{page.Name}.{handler} lowered {flag}");
    }

    public static TheoryData<Type, string> ConfirmPages => new()
    {
        { typeof(IntuneDevices), "actingDeviceId" },
        { typeof(RiskyUsers), "actingUserId" },
    };

    [Theory]
    [MemberData(nameof(ConfirmPages))]
    public void AStaleConfirmWithNothingStaged_IsANoOp_NotAThrow(Type page, string _)
    {
        // The first Confirm cleared confirmAction; the second hands the cleared value over.
        var instance = Activator.CreateInstance(page)!;

        var stale = Invoke(instance, "ExecuteActionAsync", new object?[] { null });

        Assert.True(stale.IsCompletedSuccessfully, $"{page.Name}: a Confirm with nothing staged did not return at once");
    }

    [Theory]
    [MemberData(nameof(ConfirmPages))]
    public void ASecondConfirmWhileAnActionRuns_IsIgnored(Type page, string actingFlag)
    {
        var instance = Activator.CreateInstance(page)!;
        page.GetField(actingFlag, PrivateInstance)!.SetValue(instance, "the first click's target");
        var action = Enum.GetValues(page.GetMethod("ExecuteActionAsync", PrivateInstance)!.GetParameters()[1].ParameterType
            .GetGenericArguments()[0]).GetValue(0);

        var second = Invoke(instance, "ExecuteActionAsync", action);

        Assert.True(second.IsCompletedSuccessfully, $"{page.Name}: a second Confirm ran beside the first");
        Assert.Equal("the first click's target", page.GetField(actingFlag, PrivateInstance)!.GetValue(instance));
    }

    [Theory]
    [InlineData("IntuneDevices.razor", "device")]
    [InlineData("RiskyUsers.razor", "user")]
    public void TheConfirmButton_HandsOverWhatIsStaged_NeverReadsItsValueAtTheClick(string page, string entity)
    {
        // The lambda is evaluated at the click, after the first Confirm cleared confirmAction: .Value there threw.
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page));

        Assert.Contains($"@onclick=\"() => ExecuteActionAsync({entity}, confirmAction)\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"ExecuteActionAsync({entity}, confirmAction.Value)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CancelJob")]
    [InlineData("RemoveJob")]
    public void TheJobButtons_AreDisabledWhileACancelOrRemovalRuns(string handler)
    {
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminBulkJobs.razor"));

        var line = page.Split('\n').Single(l => l.Contains($"@onclick=\"() => {handler}(job)\"", StringComparison.Ordinal));
        Assert.Contains("disabled=\"@isActing\"", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a handler checks before its guard would matter, set so that - guard or not - the handler would go on: a file for
    /// a bulk run, a staged preview for a CSV apply, a listed user for a reset. Without the guard each reaches its yield.
    /// </summary>
    private static void PrepareFormFor(object page, string handler)
    {
        var type = page.GetType();
        void Set(string field, object? value) => type.GetField(field, PrivateInstance)!.SetValue(page, value);

        switch (type.Name, handler)
        {
            case (_, "ProcessBulk"):
                Set("csvFile", NSubstitute.Substitute.For<Microsoft.AspNetCore.Components.Forms.IBrowserFile>());
                break;
            case (nameof(GroupManagement), "AddMember"):
                Set("selectedGroup", RuntimeHelpers.GetUninitializedObject(typeof(GroupInfo)));
                Set("newMember", "someone");
                break;
            case (nameof(GroupManagement), "RemoveMember"):
                Set("selectedGroup", RuntimeHelpers.GetUninitializedObject(typeof(GroupInfo)));
                break;
        }
    }

    private static Task Invoke(object page, string handler, params object?[] extra)
    {
        var method = page.GetType().GetMethod(handler, PrivateInstance)!;
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = Nullable.GetUnderlyingType(parameters[i].ParameterType) ?? parameters[i].ParameterType;
            arguments[i] = i == 1 && extra.Length > 0 ? extra[0]
                : parameterType.IsEnum ? Enum.GetValues(parameterType).GetValue(0)
                : RuntimeHelpers.GetUninitializedObject(parameterType);
        }
        return (Task)method.Invoke(page, arguments)!;
    }
}
