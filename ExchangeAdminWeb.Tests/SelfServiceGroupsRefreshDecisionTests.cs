using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-74 (3), Self-Service Groups. After a single add or remove, <c>ChangeMember</c> and <c>RemoveListedMember</c> refresh
/// the member list when the change succeeded, and they read that from <c>changeResult</c> - the result banner's field,
/// which the banner's dismiss nulls and which stays clickable while they run. Their activities already completed from
/// their own local, <c>completed</c>; the refresh is now decided from it too.
/// </summary>
/// <remarks>
/// <para>
/// Not reachable at the commit this was found on, and said so: <c>ShowFor</c> assigns <c>changeResult</c> and the refresh
/// test reads it with no await between them, so the dismiss - a browser event, dispatched only when the handler yields -
/// cannot land in that stretch, and a dismiss while the write is awaited nulls a field the handler has already nulled.
/// <c>ClickGateRegistry</c>'s exemption for that dismiss (SelfServiceGroups 300) rested on exactly this. The change
/// removes the dependency: an await placed between the two would have skipped the refresh silently. prog-7 is the
/// precedent - the same read, safe as written, moved to a local.
/// </para>
/// <para>
/// So the guard reads the source, as <c>ClickGateTests</c>' snapshot obligations do: once the handler has yielded it does
/// not read <c>changeResult</c>, and its refresh is still there, decided from <c>completed</c>. A behaviour test cannot
/// tell the two apart today - that is the finding's own premise failing - and a successful self-service write has no
/// stand-in in this suite.
/// </para>
/// </remarks>
public sealed class SelfServiceGroupsRefreshDecisionTests
{
    private const string Page = "SelfServiceGroups.razor";

    [Theory]
    [InlineData("RemoveListedMember")]
    [InlineData("ChangeMember")]
    public void TheMemberListRefresh_IsDecidedFromTheChangesOwnOutcome_NotTheBannersField(string handler)
    {
        var source = ClickGateSource.Load(Page); // comments blanked, so a comment naming the field is not a read
        var body = source.MethodBody(handler);
        Assert.StartsWith($"private async Task {handler}(", body, StringComparison.Ordinal);

        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(firstAwait >= 0, $"{Page}: {handler} no longer awaits anything. Re-check it and this test together.");
        var afterAwait = body[firstAwait..];
        var offset = source.Text.IndexOf(body, StringComparison.Ordinal);

        // A read is the field named anywhere but on the left of an assignment.
        var reads = Regex.Matches(afterAwait, ClickGateSource.WholeWord("changeResult") + @"(?!\s*=(?!=))")
            .Select(match => source.LineAt(offset + firstAwait + match.Index))
            .ToList();
        Assert.True(reads.Count == 0,
            $"{Page}: {handler} reads changeResult after its first await, at line(s) {string.Join(", ", reads)}. The "
            + "banner's dismiss can null it whenever the handler is suspended, and a refresh decided from it is then "
            + "skipped after a successful write. Decide from completed.");

        // And the refresh is still made, from the local - "stop refreshing" would satisfy the assertion above.
        Assert.Matches(
            @"if\s*\(\s*completed\?\.Success\s*==\s*true\s*&&\s*ReferenceEquals\(\s*selected\s*,\s*group\s*\)\s*\)\s*await\s+LoadMembers\(\s*\)\s*;",
            afterAwait);
    }
}
