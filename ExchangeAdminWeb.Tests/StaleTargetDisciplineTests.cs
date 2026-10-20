namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit findings A13 to A16, four instances of one discipline: an action must act on the
/// subject it was prepared for, not on whatever the editable state says when it finally runs.
/// A7 was the Wave 1 instance of the same class.
/// </summary>
/// <remarks>
/// Source scans. None of these pages has a harness - the reflection harness that drives a
/// live component exists only for Comms-10k - so nothing here proves the behaviour in a
/// browser. What they pin is the specific property each fix rests on, and after the aud-a31
/// review showed scans of this shape passing against broken code, each one checks the ORDER
/// or ABSENCE of a named expression inside a bounded method body, rather than that some
/// string appears somewhere in the file.
///
/// All read <see cref="RazorSyntax.CommentsBlanked"/>: string literals kept, comments blanked
/// - the comments on these fixes name the very fields being asserted on.
/// </remarks>
public class StaleTargetDisciplineTests
{
    private static string Page(params string[] parts) =>
        RazorSyntax.CommentsBlanked(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(parts)));

    /// <summary>
    /// One method's body, bounded by the next member declaration - or by end of file, since
    /// some of these handlers are the last member in their page.
    /// </summary>
    private static string Body(string page, string signature)
    {
        var start = page.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, signature + " not found - this guard is stale.");

        var marker = "\n    private ";
        var end = page.IndexOf(marker, start + 1, StringComparison.Ordinal);
        return page[start..(end > start ? end : page.Length)];
    }

    private static void CaptureComesFirst(string body, string capture, string what)
    {
        var at = body.IndexOf(capture, StringComparison.Ordinal);
        Assert.True(at > 0, what + " must capture before it awaits.");

        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(firstAwait > at, "The capture must come before the first await in " + what + ".");
    }

    // ---- A13: MFA Reset ----------------------------------------------------------------------

    [Fact]
    public void MfaReset_ActsOnTheUserWhoseMethodsAreListed_NotTheEditableBox()
    {
        // The defect: ExecuteReset re-read userPrincipalName, so listing A, editing the box to
        // B and resetting wiped B's methods while the operator looked at A's list - and the
        // audit recorded A's method types against B.
        var body = Body(Page("Components", "Pages", "MfaReset.razor"), "private async Task ExecuteReset()");

        Assert.DoesNotContain("userPrincipalName", body, StringComparison.Ordinal);
        Assert.Contains("listedUpn", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MfaReset_RefusesWhenNothingIsListed()
    {
        // Falling back to the box when nothing is listed would reinstate the defect by the back
        // door, so the empty case must refuse rather than default.
        var body = Body(Page("Components", "Pages", "MfaReset.razor"), "private async Task ExecuteReset()");

        var guard = body.IndexOf("if (string.IsNullOrWhiteSpace(upn))", StringComparison.Ordinal);
        Assert.True(guard > 0, "ExecuteReset must refuse when no user is listed.");

        var returns = body.IndexOf("return;", guard, StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", guard, StringComparison.Ordinal);
        Assert.True(returns > 0 && returns < firstAwait, "The refusal must return before anything is awaited.");
    }

    [Fact]
    public void MfaReset_NamesTheListedUserOnScreen_NotTheBox()
    {
        var page = Page("Components", "Pages", "MfaReset.razor");

        Assert.Contains("Confirm MFA Reset for @listedUpn", page, StringComparison.Ordinal);
        Assert.Contains("Authentication Methods for @listedUpn", page, StringComparison.Ordinal);
    }

    // ---- A14: AD Attribute Editor ------------------------------------------------------------

    [Fact]
    public void AttributeSave_ComparesTheShownAccountByIdNotByReference()
    {
        // Review of A14 caught the first version using ReferenceEquals. Every lookup builds a
        // NEW principal instance and search stays enabled during a save, so re-searching the
        // SAME account made the check false and the page kept showing stale values while
        // reporting success.
        var body = Body(Page("Components", "Pages", "ADAttributeEditor.razor"), "private async Task ConfirmSave()");

        Assert.DoesNotContain("ReferenceEquals(", body, StringComparison.Ordinal);
        Assert.Contains("shown.ObjectGuid, target.ObjectGuid", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeSave_CapturesTheChangesItIsApplying()
    {
        // Review of A14. A concurrent search clears pendingChanges, and proposedValues was
        // built from the live collection after the yield - turning a real save into
        // "successfully updated 0 attributes".
        const string capture = "var changes = pendingChanges.ToList();";
        var body = Body(Page("Components", "Pages", "ADAttributeEditor.razor"), "private async Task ConfirmSave()");

        CaptureComesFirst(body, capture, "ConfirmSave");

        var after = body[body.IndexOf("await ", StringComparison.Ordinal)..];
        Assert.DoesNotContain("pendingChanges.Select", after, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingChanges.Where", after, StringComparison.Ordinal);
        Assert.DoesNotContain("in pendingChanges", after, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeSave_NamesTheCapturedTargetEvenWhenItThrows()
    {
        // Review of A14: the outer catch still emailed lookupResult?.Principal, so a save on A
        // that threw while a search displayed B sent a failure notification naming B.
        var body = Body(Page("Components", "Pages", "ADAttributeEditor.razor"), "private async Task ConfirmSave()");

        Assert.DoesNotContain("lookupResult?.Principal?.UserPrincipalName ?? searchIdentity }", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeSave_WritesToThePrincipalItCaptured_NotTheCurrentLookup()
    {
        // A concurrent search nulls lookupResult and then replaces it with a different user.
        // Every use sat after an await, so the write itself could be aimed at B while the
        // operator was reviewing A, and the message and admin email would name B.
        const string capture = "var target = lookupResult?.Principal;";
        var body = Body(Page("Components", "Pages", "ADAttributeEditor.razor"), "private async Task ConfirmSave()");

        CaptureComesFirst(body, capture, "ConfirmSave");

        var after = body[body.IndexOf(capture, StringComparison.Ordinal)..];
        Assert.DoesNotContain("lookupResult!.Principal", after, StringComparison.Ordinal);
        Assert.DoesNotContain("lookupResult.Principal.", after, StringComparison.Ordinal);

        // The one permitted later mention: proving the panel still shows the same user before
        // writing the applied values back into its form.
        Assert.Contains("shown.ObjectGuid, target.ObjectGuid", after, StringComparison.Ordinal);
    }

    // ---- A15: Licensing Updates --------------------------------------------------------------

    [Fact]
    public void LicensingApply_UsesWhatItCapturedBeforeTheWrite()
    {
        // The defect: Cancel (ResetForm) nulls previewResult and clears ticketNumber, and it
        // was clickable throughout the apply. A click during the write made
        // previewResult.LicenseType throw INSIDE the notification try, whose catch logged an
        // email failure and moved on - so the licences changed and the required admin
        // notification was silently dropped.
        const string capture = "var preview = previewResult;";
        var body = Body(Page("Components", "Pages", "LicensingUpdates.razor"), "private async Task RunApply()");

        CaptureComesFirst(body, capture, "RunApply");

        // From the first await onward - the captures themselves necessarily read the fields,
        // so the question is whether anything does so once the handler can be interrupted.
        var after = body[body.IndexOf("await ", StringComparison.Ordinal)..];
        Assert.DoesNotContain("previewResult.", after, StringComparison.Ordinal);
        Assert.DoesNotContain("ticketNumber.Trim()", after, StringComparison.Ordinal);
        Assert.DoesNotContain("applyResult.", after, StringComparison.Ordinal);
    }

    [Fact]
    public void LicensingCancel_IsNotClickableDuringAnApply()
    {
        // The capture makes the run survive a mid-flight Cancel; this stops one being offered.
        var page = Page("Components", "Pages", "LicensingUpdates.razor");

        Assert.Contains("@onclick=\"ResetForm\" disabled=\"@isProcessing\"", page, StringComparison.Ordinal);
    }

    // ---- A16: the country picker -------------------------------------------------------------

    [Fact]
    public void CountryPicker_IsKeyedToTheRecordBeingEdited()
    {
        // Review of A16. A value comparison cannot substitute for record identity: two records
        // holding the same countries look identical to it, so an unapplied selection on A rode
        // across onto B. The key makes Blazor build a fresh picker per record.
        var host = Page("Components", "Pages", "NamedLocations.razor");

        Assert.Contains("<CountryCodePicker @key=", host, StringComparison.Ordinal);
    }

    [Fact]
    public void CountryPicker_ResyncsWhenItsBoundValueChanges()
    {
        // The defect: a one-shot _initialized latch. A picker reused for a different record
        // kept the previous record's selection, so applying it wrote A's countries onto B.
        var component = Page("Components", "Shared", "CountryCodePicker.razor");

        Assert.DoesNotContain("_initialized", component, StringComparison.Ordinal);

        var body = Body(component, "protected override void OnParametersSet()");
        Assert.Contains("_syncedFrom, Value", body, StringComparison.Ordinal);
        Assert.Contains("_syncedFrom = Value;", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private async Task Apply()")]
    [InlineData("private async Task ClearAll()")]
    public void CountryPicker_DoesNotTreatItsOwnOutputAsARecordChange(string emitter)
    {
        // Emitting without recording what was emitted means the parameter set that echoes our
        // own value looks like the record changing, and the selection is rebuilt mid-edit.
        var body = Body(Page("Components", "Shared", "CountryCodePicker.razor"), emitter);

        var record = body.IndexOf("_syncedFrom =", StringComparison.Ordinal);
        var emit = body.IndexOf("ValueChanged.InvokeAsync", StringComparison.Ordinal);
        Assert.True(record > 0 && record < emit, emitter + " must record what it emits before emitting it.");
    }
}
