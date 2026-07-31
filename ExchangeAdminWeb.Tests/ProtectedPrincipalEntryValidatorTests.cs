using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the decision the Protected Principals admin page makes about one typed entry
/// (docs/ProtectedPrincipalInputValidation-Plan.md).
///
/// The page saved any typed string before this: the AD autocomplete on Users and Groups only
/// suggested, and the add handlers never checked that the value came from a suggestion. An entry
/// matching no directory object is not inert - a bad user or OU row silently matches nothing, and
/// a bad GROUP row makes group expansion fail closed, turning every later check into a denial that
/// reads as a directory fault.
///
/// The load-bearing property is that a refusal for "no such object" and a refusal for "the lookup
/// never ran" stay distinct. Collapsing them tells an admin their correct entry was a typo during
/// an outage.
///
/// Page markup is not testable here (no bUnit harness), which is why this logic lives in a service.
/// </summary>
public class ProtectedPrincipalEntryValidatorTests
{
    private static readonly string[] Empty = [];

    private static DirectoryValidationResult Found(
        string? dn = "CN=Jane,OU=Users,DC=contoso,DC=com",
        string? upn = "jane@contoso.com",
        string? mail = "jane@contoso.com")
        => new(DirectoryLookupOutcome.Found,
               new ADSearchResult("Jane", dn ?? "", "jane", upn, mail, "User"));

    private static DirectoryValidationResult NotFound
        => new(DirectoryLookupOutcome.NotFound, null);

    private static DirectoryValidationResult Unavailable
        => new(DirectoryLookupOutcome.Unavailable, null);

    // ---- the two refusals must not be confusable ----------------------------

    [Fact]
    public void Decide_DirectoryUnreachable_RefusesAndSaysRetry_NotTypo()
    {
        // THE fail-closed case. The lookup never ran, so blaming the operator's spelling would
        // send them chasing a mistake they did not make.
        var d = ProtectedPrincipalEntryValidator.Decide(Empty, "ceo@contoso.com", "User", Unavailable);

        Assert.False(d.Accepted);
        Assert.Equal(ProtectedPrincipalEntryValidator.UnavailableMessage, d.ErrorMessage);
        Assert.Contains("unreachable", d.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", d.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_NotInDirectory_RefusesAndSaysCheckTheName()
    {
        var d = ProtectedPrincipalEntryValidator.Decide(Empty, "typo@contoso.com", "User", NotFound);

        Assert.False(d.Accepted);
        Assert.Contains("was not found in Active Directory", d.ErrorMessage!);
        Assert.Contains("typo@contoso.com", d.ErrorMessage!);
        Assert.DoesNotContain("unreachable", d.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_TheTwoRefusalMessagesDiffer()
    {
        // Stated directly because the whole design rests on it.
        var unavailable = ProtectedPrincipalEntryValidator.Decide(Empty, "x@contoso.com", "User", Unavailable);
        var notFound = ProtectedPrincipalEntryValidator.Decide(Empty, "x@contoso.com", "User", NotFound);

        Assert.NotEqual(unavailable.ErrorMessage, notFound.ErrorMessage);
    }

    [Fact]
    public void Decide_NotFoundMessage_MentionsTheCloudOnlyBoundary()
    {
        // The O365-group case that prompted this work: refusing it is correct behavior, and the
        // message has to say why rather than looking like a bug.
        var d = ProtectedPrincipalEntryValidator.Decide(Empty, "cloudgroup@contoso.com", "Group", NotFound);

        Assert.Contains("cloud-only", d.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- a refusal must not eat what the operator typed ---------------------

    [Theory]
    [InlineData(DirectoryLookupOutcome.NotFound)]
    [InlineData(DirectoryLookupOutcome.Unavailable)]
    public void Decide_AnyRefusal_LeavesTheInputBoxAlone(DirectoryLookupOutcome outcome)
    {
        // Clearing the box on refusal would destroy the text the operator needs to correct.
        var d = ProtectedPrincipalEntryValidator.Decide(
            Empty, "someone@contoso.com", "User", new DirectoryValidationResult(outcome, null));

        Assert.False(d.ClearInput);
    }

    [Fact]
    public void Decide_Accepted_ClearsTheInputBox()
    {
        var d = ProtectedPrincipalEntryValidator.Decide(Empty, "jane@contoso.com", "User", Found());

        Assert.True(d.Accepted);
        Assert.True(d.ClearInput);
        Assert.Null(d.ErrorMessage);
    }

    // ---- no pointless directory round-trips ---------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldConsultDirectory_BlankInput_IsFalse(string raw)
    {
        Assert.False(ProtectedPrincipalEntryValidator.ShouldConsultDirectory(Empty, raw));
    }

    [Fact]
    public void ShouldConsultDirectory_AlreadyListed_IsFalse()
    {
        string[] existing = ["ceo@contoso.com"];
        Assert.False(ProtectedPrincipalEntryValidator.ShouldConsultDirectory(existing, "CEO@Contoso.com"));
    }

    [Fact]
    public void ShouldConsultDirectory_NewEntry_IsTrue()
    {
        string[] existing = ["ceo@contoso.com"];
        Assert.True(ProtectedPrincipalEntryValidator.ShouldConsultDirectory(existing, "cfo@contoso.com"));
    }

    [Fact]
    public void Decide_Duplicate_IsNotAnError_AndClearsTheBox()
    {
        string[] existing = ["ceo@contoso.com"];
        var d = ProtectedPrincipalEntryValidator.Decide(existing, "CEO@Contoso.com", "User", NotFound);

        Assert.False(d.Accepted);
        Assert.Null(d.ErrorMessage);     // already protected - nothing is wrong
        Assert.True(d.ClearInput);
        Assert.False(d.ConsultedDirectory);
    }

    [Fact]
    public void Decide_BlankInput_IsSilentlyIgnored()
    {
        var d = ProtectedPrincipalEntryValidator.Decide(Empty, "   ", "User", NotFound);

        Assert.False(d.Accepted);
        Assert.Null(d.ErrorMessage);
        Assert.False(d.ConsultedDirectory);
    }

    // ---- flagging already-saved entries (slice 4) ---------------------------

    [Fact]
    public void ShouldFlagAsStale_NotFound_Flags()
    {
        Assert.True(ProtectedPrincipalEntryValidator.ShouldFlagAsStale(DirectoryLookupOutcome.NotFound));
    }

    [Fact]
    public void ShouldFlagAsStale_DirectoryUnreachable_StaysSilent()
    {
        // THE case. During an outage every saved entry fails to resolve at once; badging them all
        // would read as "your protection rules have been lost" - alarming and false. Silence is
        // correct, so the badge's absence means "not known to be stale", never "verified".
        Assert.False(ProtectedPrincipalEntryValidator.ShouldFlagAsStale(DirectoryLookupOutcome.Unavailable));
    }

    [Fact]
    public void ShouldFlagAsStale_Found_DoesNotFlag()
    {
        Assert.False(ProtectedPrincipalEntryValidator.ShouldFlagAsStale(DirectoryLookupOutcome.Found));
    }

    [Fact]
    public void StaleFlagging_And_EntryRefusal_TreatAFailedLookupOppositely_ByDesign()
    {
        // Both rules follow from "a directory that did not answer is not evidence about the
        // object", but they point in opposite directions: a failed lookup REFUSES a new entry
        // while staying SILENT about an existing one. Pinned so a later refactor does not
        // "simplify" them into one shared helper.
        var refusesNewEntry = !ProtectedPrincipalEntryValidator
            .Decide(Empty, "x@contoso.com", "User", Unavailable).Accepted;
        var silentOnExisting = !ProtectedPrincipalEntryValidator
            .ShouldFlagAsStale(DirectoryLookupOutcome.Unavailable);

        Assert.True(refusesNewEntry);
        Assert.True(silentOnExisting);
    }

    // ---- save vs in-flight validation (ppv-3) -------------------------------

    [Fact]
    public void ShouldBlockSave_ValidationInFlight_Refuses()
    {
        // Review finding ppv-3. Add validates on a background task, so the circuit stays free and
        // the operator can click Save mid-check. Saving then persists the list WITHOUT the pending
        // entry, reports success, and the entry appears in the page moments later - store and page
        // disagree, and nothing says so until a reload loses it.
        Assert.True(ProtectedPrincipalEntryValidator.ShouldBlockSave(validationInFlight: true));
    }

    [Fact]
    public void ShouldBlockSave_NothingInFlight_Allows()
    {
        Assert.False(ProtectedPrincipalEntryValidator.ShouldBlockSave(validationInFlight: false));
    }

    [Fact]
    public void SaveBlockedMessage_TellsTheOperatorToWaitAndRetry()
    {
        // The refusal is transient, so the message must say so rather than reading as a failure.
        Assert.Contains("Wait", ProtectedPrincipalEntryValidator.SaveBlockedMessage);
        Assert.Contains("save", ProtectedPrincipalEntryValidator.SaveBlockedMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---- store what the protection engine will resolve ----------------------

    [Fact]
    public void CanonicalValue_Group_StoresTheDistinguishedName()
    {
        // MatchesDnToProtectedGroup compares the stored value against a resolved DN, so storing
        // the typed display name would leave the rule dependent on name-format luck.
        var match = new ADSearchResult(
            "VR Staff", "CN=VR Staff,OU=Groups,DC=contoso,DC=com", "VRStaff", null, null, "Group");

        Assert.Equal(
            "CN=VR Staff,OU=Groups,DC=contoso,DC=com",
            ProtectedPrincipalEntryValidator.CanonicalValue("VR Staff", "Group", match));
    }

    [Fact]
    public void CanonicalValue_User_PrefersTheUpnOverWhatWasTyped()
    {
        var match = new ADSearchResult(
            "Jane", "CN=Jane,DC=contoso,DC=com", "jane", "jane.doe@contoso.com", "jd@contoso.com", "User");

        Assert.Equal(
            "jane.doe@contoso.com",
            ProtectedPrincipalEntryValidator.CanonicalValue("CONTOSO\\jane", "User", match));
    }

    [Fact]
    public void CanonicalValue_UserWithoutUpn_FallsBackToMail()
    {
        var match = new ADSearchResult(
            "Jane", "CN=Jane,DC=contoso,DC=com", "jane", null, "jane@contoso.com", "User");

        Assert.Equal(
            "jane@contoso.com",
            ProtectedPrincipalEntryValidator.CanonicalValue("jane", "User", match));
    }

    [Fact]
    public void CanonicalValue_DirectoryGaveNothingUsable_KeepsTheTypedValue()
    {
        var match = new ADSearchResult("Jane", "", "jane", null, null, "User");

        Assert.Equal("jane", ProtectedPrincipalEntryValidator.CanonicalValue("jane", "User", match));
    }

    [Fact]
    public void CanonicalValue_NoMatchObject_KeepsTheTypedValue()
    {
        Assert.Equal("jane", ProtectedPrincipalEntryValidator.CanonicalValue("jane", "User", null));
    }

    [Fact]
    public void Decide_Accepted_AddsTheCanonicalFormNotTheTypedForm()
    {
        var d = ProtectedPrincipalEntryValidator.Decide(
            Empty, "CONTOSO\\jane", "User", Found(upn: "jane.doe@contoso.com"));

        Assert.True(d.Accepted);
        Assert.Equal("jane.doe@contoso.com", d.ValueToAdd);
    }

    [Fact]
    public void Decide_Ou_StoresTheDistinguishedName()
    {
        var match = new DirectoryValidationResult(
            DirectoryLookupOutcome.Found,
            new ADSearchResult("Tier0", "OU=Tier0,DC=contoso,DC=com", null, null, null, "OU"));

        var d = ProtectedPrincipalEntryValidator.Decide(Empty, "OU=Tier0,DC=contoso,DC=com", "OU", match);

        Assert.True(d.Accepted);
        Assert.Equal("OU=Tier0,DC=contoso,DC=com", d.ValueToAdd);
    }
}
