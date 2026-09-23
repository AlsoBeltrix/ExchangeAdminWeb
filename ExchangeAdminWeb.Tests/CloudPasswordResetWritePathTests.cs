namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-text guards on the write path (docs/CloudPasswordReset-Plan.md S6).
/// </summary>
/// <remarks>
/// A Blazor handler needs a circuit to execute, so these assert its SHAPE and, where it matters,
/// its ORDER - the same approach ClickGateStuckFlagTests and DeployInvariantsTests take. Ordering
/// is the whole design here: every gate that can refuse must run before the PATCH, because the
/// write cannot be undone.
///
/// Stated as a limitation rather than passed off as equivalent: these prove the calls are present
/// and sequenced, not that they behave correctly at runtime. The behavioural coverage is in the
/// service tests, where the logic lives as pure functions.
/// </remarks>
public class CloudPasswordResetWritePathTests
{
    private static string Page() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "CloudPasswordReset.razor"));

    private static int IndexOf(string haystack, string needle)
    {
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{needle}' not found - this guard is pinned to code that no longer exists.");
        return i;
    }

    private static string HandlerBody()
    {
        var page = Page();
        return page[IndexOf(page, "private async Task ExecuteResetAsync()")..];
    }

    [Fact]
    public void Authorization_is_rechecked_immediately_before_the_write()
    {
        // The page attribute and the OnInitializedAsync check are navigation control. Neither
        // survives a direct event invocation over the circuit.
        var body = HandlerBody();

        var recheck = IndexOf(body, "AuthorizeAsync(authState.User, \"CloudPasswordReset\")");
        var patch = IndexOf(body, "ResetPasswordAsync(");

        Assert.True(recheck < patch, "The authorization re-check must precede the password write.");
    }

    [Fact]
    public void Every_refusable_gate_runs_before_the_write()
    {
        // The ordering rule. Once the password has changed it cannot be un-set, so a gate that
        // refuses afterwards leaves an account whose password nobody knows.
        var body = HandlerBody();
        var patch = IndexOf(body, "ResetPasswordAsync(");

        foreach (var gate in new[]
                 {
                     "AuthorizeAsync(authState.User, \"CloudPasswordReset\")",
                     "ValidateTicketAsync(ticket)",
                     "CheckProtectionAsync(",
                     "DeriveDestination(target)",
                     "Email.UserNotificationsEnabled",
                 })
        {
            Assert.True(IndexOf(body, gate) < patch, $"{gate} must run before the password write.");
        }
    }

    [Fact]
    public void The_destination_is_re_derived_at_write_time_not_taken_from_preflight()
    {
        // The directory can change between the preflight and the confirm. A stale destination is
        // a password mailed somewhere nobody currently chose.
        Assert.Contains("destination = ResetService.DeriveDestination(target);", HandlerBody());
    }

    [Fact]
    public void A_failed_send_discards_the_password_rather_than_showing_it()
    {
        // Owner ruling 2026-09-10: "if the send itself fails, then fail closed." Showing it here
        // would hand every operator a way to see a password by provoking a send failure.
        var body = HandlerBody();
        var sendFailed = IndexOf(body, "if (!sent)");
        var block = body.Substring(sendFailed, Math.Min(900, body.Length - sendFailed));

        Assert.DoesNotContain("revealedPassword = outcome.Password", block);
        Assert.Contains("CloudPasswordReset_DeliveryFailed", block);
    }

    [Fact]
    public void The_reveal_path_requires_the_reveal_permission_server_side()
    {
        var body = HandlerBody();

        var revealAuth = IndexOf(body, "AuthorizeAsync(authState.User, \"CloudPasswordResetReveal\")");
        var reveal = IndexOf(body, "revealedPassword = outcome.Password");

        Assert.True(revealAuth < reveal, "The reveal permission must be checked before a password is shown.");
    }

    [Fact]
    public void The_audit_builder_never_touches_the_password()
    {
        // AC7, enforced by source text rather than inspection. The password's only destinations
        // are the PATCH body and the owner's mail.
        var page = Page();
        var audit = page[IndexOf(page, "private void AuditReset(")..];
        var auditBlock = audit[..IndexOf(audit, "private async Task NotifyAdminsAsync(")];

        // The VALUE, not the word: forceChangePasswordNextSignIn is a legitimate field name and
        // an assertion that banned the substring "Password" would ban it too. These two
        // identifiers are the only things that ever hold the generated password.
        Assert.DoesNotContain("outcome.Password", auditBlock);
        Assert.DoesNotContain("revealedPassword", auditBlock);
    }

    [Fact]
    public void The_admin_notification_never_carries_the_password()
    {
        var page = Page();
        var notify = page[IndexOf(page, "private async Task NotifyAdminsAsync(")..];
        var notifyBlock = notify[..IndexOf(notify, "DestinationRefusalText")];

        Assert.DoesNotContain("outcome.Password", notifyBlock);
        Assert.DoesNotContain("revealedPassword", notifyBlock);
    }

    [Fact]
    public void The_serviced_note_rides_extra_not_errorDetail()
    {
        // LogModuleAction writes ["error"] = success ? null : errorDetail, so a note passed as
        // errorDetail on a success is silently discarded - the failure that lost an
        // authorised-servicer record once already.
        Assert.Contains("[\"protectedPrincipalServiced\"] = servicedNote,", Page());
    }

    [Fact]
    public void Every_audit_event_carries_the_same_field_set_from_one_builder()
    {
        // AC18: one search over the category returns uniform records, so a missing field always
        // means a bug rather than a branch that did not bother.
        var page = Page();

        foreach (var key in new[]
                 {
                     "targetObjectId", "targetCloudOnly", "targetDirectoryRoles",
                     "destinationAddress", "destinationEmployeeId",
                     "forceChangePasswordNextSignIn", "passwordDelivery", "revealUsed",
                     "refusalReason", "protectedPrincipalServiced",
                 })
        {
            Assert.Contains($"[\"{key}\"]", page);
        }

        // Exactly one place builds them. A second would drift from the first.
        Assert.Equal(1, page.Split("[\"passwordDelivery\"]").Length - 1);
    }

    [Fact]
    public void Protection_uses_the_exchange_fallback_and_passes_the_real_entra_object_id()
    {
        // The AD-only resolve reports every cloud-only object as NotFound, which for this module
        // is every target. And unlike MfaReset, this module HAS resolved the object id by the time
        // protection runs, so the unresolved branch must not copy MfaReset's null.
        var page = Page();

        Assert.Contains("ResolveWithExchangeFallbackAsync(upn)", page);
        Assert.Contains("EntraObjectId: entraObjectId", page);
        Assert.DoesNotContain("EntraObjectId: null", page);
    }

    [Fact]
    public void An_audit_failure_does_not_change_the_operation_result()
    {
        var page = Page();
        var audit = page[IndexOf(page, "private void AuditReset(")..];
        var auditBlock = audit[..IndexOf(audit, "private async Task NotifyAdminsAsync(")];

        Assert.Contains("catch (Exception ex)", auditBlock);
    }

    [Fact]
    public void A_notification_failure_does_not_change_the_operation_result()
    {
        var page = Page();
        var notify = page[IndexOf(page, "private async Task NotifyAdminsAsync(")..];
        var notifyBlock = notify[..IndexOf(notify, "DestinationRefusalText")];

        Assert.Contains("catch (Exception ex)", notifyBlock);
    }

    [Fact]
    public void Module_is_opted_into_protected_principal_servicing()
    {
        // AC9, and the ppsvc-1 recurrence: the NoteFor call exists in code but grants nothing from
        // the admin UI unless the module is in this list. Same commit, by that file's own rule.
        var config = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"));

        Assert.Contains("\"CloudPasswordReset\",", config);
    }

    [Fact]
    public void An_undelivered_password_is_logged_at_critical()
    {
        // The one state in this module where a human should be told without going looking: the
        // password changed and nobody knows it (plan, Diagnostic logging).
        Assert.Contains("LogCritical", HandlerBody());
    }
}

/// <summary>
/// Every module's sidebar icon class must actually exist in the host CSS (review finding cpr-7).
/// </summary>
/// <remarks>
/// A descriptor can name any class it likes and nothing complains: the nav renders the string
/// straight through, so an undefined class is an invisible icon that nobody notices until a
/// screenshot. Cloud Password Reset shipped with `bi-key-fill-nav-menu`, which is not defined
/// anywhere in this app.
/// </remarks>
public class ModuleIconClassTests
{
    [Fact]
    public void Every_descriptor_icon_class_is_defined_in_the_host_css()
    {
        var css = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("wwwroot", "app.css"));
        var catalog = new ExchangeAdminWeb.Modules.ModuleCatalog();

        var undefined = new List<string>();

        // Config-only modules are excluded from the operational sidebar by the module contract
        // (docs/AdminModuleSpec.md), so their icon class is never rendered and an undefined one
        // costs nothing. ExchangeOnline is in exactly that position today - it names
        // .bi-cloud-fill-nav-menu, which is not defined, and it does not matter. Exempting them is
        // honest about what this guard protects; "fixing" its CSS would be a drive-by on an
        // unrelated file to satisfy a test that should not have been asking.
        foreach (var module in catalog.GetAll().Where(m => !m.IsConfigOnly))
        {
            var navClass = module.IconCss
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(c => c.EndsWith("-nav-menu", StringComparison.Ordinal));

            if (navClass is null)
            {
                undefined.Add($"{module.Id}: no -nav-menu class in '{module.IconCss}'");
                continue;
            }

            if (!css.Contains($".{navClass}", StringComparison.Ordinal))
                undefined.Add($"{module.Id}: .{navClass} is not defined in wwwroot/app.css");
        }

        Assert.True(undefined.Count == 0,
            "these modules name a sidebar icon class the host CSS does not define, so their nav "
            + "item renders without an icon:\n  " + string.Join("\n  ", undefined));
    }
}
