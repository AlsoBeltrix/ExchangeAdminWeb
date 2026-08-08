using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards that a PAGE-level protected-principal gate cannot silently make the servicer grant
/// unreachable (finding pps-2).
/// </summary>
/// <remarks>
/// Source-level tripwires, explicitly NOT behavioural coverage. There is no bUnit harness in this
/// repo, so no test can render a Razor page or observe which branch a handler takes. Stated as
/// tripwires so nobody reads them as proof the pages behave correctly.
///
/// The rule they encode, which generalises past these two pages: **a page gate that hides the write
/// UI is part of the authorization decision, not a display detail.** Where a page checks protection
/// independently of the service, the two must ask the same question with the same module id. If the
/// page can refuse where the service would allow, the stricter one wins silently, the configured
/// grant does nothing, and the admin UI still advertises it - which is exactly how the servicer
/// capability shipped unreachable once already (2026-08-06).
///
/// Emergency Disable is included even though it was correct when written: it has the same two-gate
/// shape, and it is the module whose shape was recorded and then not applied to AD Attribute
/// Editor. Pinning both together is what stops a later edit reintroducing the gap in either one.
/// </remarks>
public sealed class PageProtectionGateServicingTests
{
    [Theory]
    [InlineData("ADAttributeEditor.razor", "ADAttributeEditor")]
    [InlineData("EmergencyDisable.razor", "EmergencyDisable")]
    public void APageThatGatesOnProtection_AlsoConsultsTheServicer(string page, string moduleId)
    {
        var source = ReadPage(page);

        // The page must consult the servicer helper, not merely hold a reference to the service.
        Assert.Contains("ProtectedPrincipalServicing.NoteFor", source, StringComparison.Ordinal);

        // With ITS OWN module id. A borrowed id would let a grant in another module authorise this
        // one, and a mismatch against the service's id would let the page allow where the service
        // refuses - the same defect in the opposite direction.
        Assert.Contains($"ServicerModuleId = \"{moduleId}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttributeEditorLookupGate_DoesNotBlockWithoutAskingTheServicer()
    {
        // The precise defect: `protectedBlocked = true` reachable directly from `IsProtected`
        // with no servicer decision in between. Anchored to the branch that sets it rather than to
        // the file as a whole, so a NoteFor call somewhere else in the page cannot satisfy it.
        var source = ReadPage("ADAttributeEditor.razor");

        var branch = Regex.Match(
            source,
            @"if \(protectionCheck\.IsProtected\)\s*\{(?<body>.*?)\n            \}",
            RegexOptions.Singleline);

        Assert.True(branch.Success, "the attribute editor's protected-principal branch was not found");

        var body = branch.Groups["body"].Value;
        Assert.Contains("ProtectedPrincipalServicing.NoteFor", body, StringComparison.Ordinal);
        Assert.Contains("protectedBlocked = true", body, StringComparison.Ordinal);

        // The refusal must be conditional on the servicer decision, never unconditional.
        Assert.Contains("is null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AServicedOperator_IsToldTheOverrideIsInEffect()
    {
        // An override that the operator cannot see is one they cannot decline to use. The audit
        // record is written either way; the banner is what makes it a decision rather than a
        // surprise. Anchored to the flag the banner is gated on, so deleting the gate fails here.
        var source = ReadPage("ADAttributeEditor.razor");

        Assert.Contains("protectedServiced = true", source, StringComparison.Ordinal);
        Assert.Contains("@if (protectedServiced)", source, StringComparison.Ordinal);
        Assert.Contains("override in effect", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheServicedFlag_IsResetBeforeEachSearch()
    {
        // A stale flag would tell an operator searching an ORDINARY user that they are acting under
        // an override - and worse, the reverse ordering would leave the banner up after a refusal.
        // This is the blr-3 shape: page state emptied for the new query but one field left behind.
        var source = ReadPage("ADAttributeEditor.razor");

        var reset = Regex.Match(
            source,
            @"protectedBlocked = false;\s*\n\s*protectedServiced = false;",
            RegexOptions.None);

        Assert.True(reset.Success,
            "protectedServiced must be cleared alongside protectedBlocked at the start of a search");
    }

    [Fact]
    public void UndoPreviewAndExecute_BothTakeTheActingPrincipal()
    {
        // Reflection over the compiled interface, not a text match: preview refusing what execute
        // would allow means an authorised servicer never gets an Undo button to press, so the
        // execute-side grant is unreachable. The two can only agree if both are given the operator.
        //
        // Honest about its own strength: removing the parameter from the interface breaks the
        // BUILD (the implementation stops satisfying it), so the compiler catches that revert
        // before this test runs. What this test adds is a statement of intent - it names why the
        // parameter is there, so a future change that makes it optional or unused has something
        // to fail against rather than looking like a harmless signature tidy-up.
        var iface = typeof(ExchangeAdminWeb.Services.IUndoableModule);

        var preview = iface.GetMethod(nameof(ExchangeAdminWeb.Services.IUndoableModule.PreviewUndoAsync));
        var execute = iface.GetMethod(nameof(ExchangeAdminWeb.Services.IUndoableModule.ExecuteUndoAsync));

        Assert.NotNull(preview);
        Assert.NotNull(execute);

        Assert.Contains(preview!.GetParameters(),
            p => p.ParameterType == typeof(System.Security.Claims.ClaimsPrincipal));
        Assert.Contains(execute!.GetParameters(),
            p => p.ParameterType == typeof(System.Security.Claims.ClaimsPrincipal));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static string ReadPage(string fileName) =>
        File.ReadAllText(Path.Combine(GetPagesDirectory(), fileName));

    private static string GetPagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var pages = Path.Combine(dir.FullName, "Components", "Pages");
            if (Directory.Exists(pages))
                return pages;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Components/Pages from the test base directory.");
    }
}
