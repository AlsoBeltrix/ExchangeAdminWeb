using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A9: the undo resolved its target from the UPN recorded in the audit event,
/// with no immutable id, so a UPN reassigned to a different person since the original edit
/// meant the undo restored one person's old values onto another's account.
/// </summary>
/// <remarks>
/// The decision is a pure function and is tested as one. Driving the whole undo service
/// reaches a live directory through <c>LookupAsync</c>, so the wiring into its two call sites
/// - preview and execute - is a source scan in
/// <see cref="StaleTargetDisciplineTests"/>'s sibling below, and stated as such on the
/// finding.
/// </remarks>
public class UndoIdentityTests
{
    private static ResolvedDirectoryPrincipal Principal(string? guid) =>
        new("AD", "Alex Example", "alex@contoso.com", "alex", "alex@contoso.com",
            "CN=alex,DC=contoso,DC=com", guid, null);

    private static Dictionary<string, object?> Audit(string? guid)
    {
        var evt = new Dictionary<string, object?> { ["target"] = "alex@contoso.com" };
        if (guid != null)
            evt["targetObjectGuid"] = guid;
        return evt;
    }

    [Fact]
    public void TheSameObject_IsAllowed()
    {
        Assert.Null(ADAttributeEditorUndoService.IdentityMismatch(
            Audit("11111111-1111-1111-1111-111111111111"),
            Principal("11111111-1111-1111-1111-111111111111"),
            "alex@contoso.com"));
    }

    [Fact]
    public void TheSameObjectInDifferentCase_IsAllowed()
    {
        // AD returns the GUID in whatever casing the provider used; a case difference is not
        // a different object, and refusing it would make undo unusable for no safety gain.
        Assert.Null(ADAttributeEditorUndoService.IdentityMismatch(
            Audit("AAAAAAAA-1111-1111-1111-111111111111"),
            Principal("aaaaaaaa-1111-1111-1111-111111111111"),
            "alex@contoso.com"));
    }

    [Fact]
    public void AReassignedName_IsRefused()
    {
        // The defect itself: the UPN still resolves, but to somebody else.
        var reason = ADAttributeEditorUndoService.IdentityMismatch(
            Audit("11111111-1111-1111-1111-111111111111"),
            Principal("22222222-2222-2222-2222-222222222222"),
            "alex@contoso.com");

        Assert.NotNull(reason);
        Assert.Contains("different directory object", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAuditRecordWithNoObjectId_IsRefusedRatherThanTrusted()
    {
        // Records written before the id was captured cannot prove anything. Refusing them
        // breaks undo for older rows, deliberately - an undo that cannot prove its target is
        // the failure this finding describes.
        var reason = ADAttributeEditorUndoService.IdentityMismatch(
            Audit(null), Principal("11111111-1111-1111-1111-111111111111"), "alex@contoso.com");

        Assert.NotNull(reason);
        Assert.Contains("predates object-identity capture", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADirectoryThatReturnsNoObjectId_IsRefused()
    {
        var reason = ADAttributeEditorUndoService.IdentityMismatch(
            Audit("11111111-1111-1111-1111-111111111111"), Principal(null), "alex@contoso.com");

        Assert.NotNull(reason);
        Assert.Contains("did not return an object id", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothUndoPathsConsultTheCheck()
    {
        // Source scan, and the limit is real: the service reaches a live directory before
        // either call site, so nothing here drives them. What it pins is that neither path
        // was left without the check.
        var source = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ADAttributeEditorUndoService.cs"));

        var calls = System.Text.RegularExpressions.Regex.Matches(source, @"IdentityMismatch\(auditEvent");
        Assert.True(calls.Count == 2, $"Expected the preview and execute paths to check; found {calls.Count}.");
    }
}
