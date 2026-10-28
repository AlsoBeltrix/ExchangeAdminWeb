using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The fill-in through the page, end to end: the preflight, then the confirm, over <see cref="CloudPasswordResetPageRun"/>
/// (docs/CloudPasswordResetEmployeeId-Plan.md S3: T15, T18-T21 at run time, and AC-F1 to AC-F6).
/// </summary>
/// <remarks>
/// What each run wrote is read back from where it went - Graph's requests, the audit file, the mail calls - so these
/// prove the behaviour the source guards below can only prove the shape of.
/// </remarks>
public class CloudPasswordResetFillPageRunTests
{
    private const string DomainA = "one.example.test";
    private const string DomainB = "two.example.test";
    private const string Number = "0001234";
    private const string SaveAlert = "Cloud password reset: employee ID";
    private const string ResetAlert = "Cloud password reset";

    /// <summary>Today's reveal-gate refusal, word for word (plan 3.3). The owner's failure mode lands on it.</summary>
    private const string RevealGateRefusal =
        "The password could not be delivered to an owner, and you do not hold the permission to see it on screen.";

    private const string GraphError =
        """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""";

    /// <summary>Two domains; the owner the account's name points to, jdoe, in the second.</summary>
    private static StandInForest Forest(out StandInForest.User owner)
    {
        var forest = new StandInForest(DomainA, DomainB);
        owner = forest.Add(DomainB, "jdoe", Number, "Jo.Doe@Example.Test", "Jo Doe");
        return forest;
    }

    /// <summary>Two domains, and nobody in either answers to the account's name.</summary>
    private static StandInForest NobodyForest() => new(DomainA, DomainB);

    private static string GuidOf(StandInForest.User user) => Guid.Parse(user.ObjectGuid!).ToString("D");

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static CloudPasswordResetPageRun.StepProgress.Handle ResetActivity(CloudPasswordResetPageRun run) =>
        run.Progress.Handles.Last(h => h.Label.StartsWith("Resetting ", StringComparison.Ordinal));

    // ----- AC-F3, and T21: the planned fill-in -----

    [Fact]
    public void A_planned_fill_in_is_shown_before_confirm_then_saves_the_number_resets_and_mails_the_owner()
    {
        var forest = Forest(out var owner);
        using var run = new CloudPasswordResetPageRun(forest);

        run.Preflight();

        // The preflight shows the number, the account it comes from and the destination, and writes nothing.
        var planned = run.Destination!.Fill!;
        Assert.Equal(EmployeeIdFillOutcome.Planned, planned.Outcome);
        Assert.Equal(Number, planned.EmployeeId);
        Assert.Equal("jdoe", planned.SourceLogonName);
        Assert.Equal("Jo Doe", planned.SourceDisplayName);
        Assert.Equal("jo.doe@example.test", run.Destination.Address);
        Assert.Empty(run.Writes);

        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        Assert.Collection(run.Writes,
            save => Assert.Equal("""{"employeeId":"0001234"}""", save.Body),
            reset => Assert.Contains("\"passwordProfile\"", reset.Body, StringComparison.Ordinal));
        Assert.All(run.Writes, write => Assert.Equal($"/v1.0/users/{CloudPasswordResetPageRun.AccountObjectId}", write.Uri.AbsolutePath));
        Assert.Equal("jo.doe@example.test", Assert.Single(run.OwnerMails)[0]);

        Assert.Collection(run.AuditRows(),
            saved =>
            {
                Assert.Equal("CloudPasswordReset_EmployeeIdSet", Text(saved, "action"));
                Assert.Equal("Success", Text(saved, "result"));
                Assert.Equal("Written", Text(saved, "employeeIdFill"));
                Assert.Equal(Number, Text(saved, "employeeIdFillValue"));
                Assert.Equal(GuidOf(owner), Text(saved, "employeeIdFillSource"));
                Assert.Equal("n/a", Text(saved, "passwordDelivery"));
                Assert.False(saved.GetProperty("revealUsed").GetBoolean());
            },
            reset =>
            {
                Assert.Equal("CloudPasswordReset_Execute", Text(reset, "action"));
                Assert.Equal("Success", Text(reset, "result"));
                Assert.Equal("Sent", Text(reset, "passwordDelivery"));
                Assert.Equal("Written", Text(reset, "employeeIdFill"));
                Assert.Equal(Number, Text(reset, "destinationEmployeeId"));
                Assert.Equal("jo.doe@example.test", Text(reset, "destinationAddress"));
            });

        var saveAlert = Assert.Single(run.AdminAlerts(SaveAlert));
        Assert.True(saveAlert.Success);
        Assert.Equal(Number, saveAlert.Details["Employee ID"]);
        Assert.Equal("jdoe (Jo Doe)", saveAlert.Details["Taken from directory account"]);
        Assert.Equal("Written", saveAlert.Details["Outcome"]);

        var resetAlert = Assert.Single(run.AdminAlerts(ResetAlert));
        Assert.Equal("Written", resetAlert.Details["Employee ID filled in"]);
        Assert.Equal("jdoe (Jo Doe)", resetAlert.Details["Filled in from"]);

        Assert.Contains($"The employee ID {Number} was saved on the account.", run.ResultMessage, StringComparison.Ordinal);
        Assert.Contains("saving the employee ID", ResetActivity(run).Steps);
    }

    [Fact]
    public void T21_the_password_goes_to_the_number_lookups_match_never_the_name_lookups()
    {
        // The directory's two answers about the owner disagree about the mailbox. Whatever the address is, it is the one
        // the NUMBER led to: the send, destinationAddress and the administrators' alert all name it.
        var forest = Forest(out var owner);
        forest.Rewrite = (query, user) => user with
        {
            Mail = query.LdapFilter.StartsWith("(sAMAccountName=", StringComparison.Ordinal)
                ? "from.the.name@example.test"
                : "from.the.number@example.test",
        };
        using var run = new CloudPasswordResetPageRun(forest);

        run.Preflight();
        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        Assert.Equal("from.the.number@example.test", Assert.Single(run.OwnerMails)[0]);

        var reset = run.AuditRows().Single(row => Text(row, "action") == "CloudPasswordReset_Execute");
        Assert.Equal("from.the.number@example.test", Text(reset, "destinationAddress"));
        Assert.Equal(GuidOf(owner), Text(reset, "employeeIdFillSource"));
        Assert.Equal("from.the.number@example.test", Assert.Single(run.AdminAlerts(ResetAlert)).Details["Destination"]);
    }

    // ----- AC-F5, T15 and T20: a save that fails never stops the reset, and is told -----

    [Fact]
    public void A_refused_save_does_not_stop_the_reset_and_the_administrators_are_told_the_number_was_not_saved()
    {
        using var run = new CloudPasswordResetPageRun(Forest(out _))
        {
            SaveAnswer = () => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(GraphError) },
        };

        run.Preflight();
        run.Confirm();

        // The reset went ahead: the refused save, then the password write and the owner's mail.
        Assert.True(run.ResultSuccess, run.ResultMessage);
        Assert.Equal(2, run.Writes.Count);
        Assert.Single(run.OwnerMails);

        Assert.Collection(run.AuditRows(),
            saved =>
            {
                Assert.Equal("CloudPasswordReset_EmployeeIdSet", Text(saved, "action"));
                Assert.Equal("Failed", Text(saved, "result"));
                Assert.Equal("WriteRejected", Text(saved, "employeeIdFill"));
                Assert.Contains("not saved", Text(saved, "error"), StringComparison.Ordinal);
                Assert.Contains("403", Text(saved, "error"), StringComparison.Ordinal);
                Assert.DoesNotContain("Insufficient privileges", Text(saved, "error"), StringComparison.Ordinal);
            },
            reset =>
            {
                Assert.Equal("CloudPasswordReset_Execute", Text(reset, "action"));
                Assert.Equal("Success", Text(reset, "result"));
                Assert.Equal("WriteRejected", Text(reset, "employeeIdFill"));
            });

        var saveAlert = Assert.Single(run.AdminAlerts(SaveAlert));
        Assert.False(saveAlert.Success);
        Assert.Equal("WriteRejected", saveAlert.Details["Outcome"]);
        Assert.Contains("not saved", saveAlert.Details["Result"], StringComparison.Ordinal);
        Assert.Contains("not saved", run.ResultMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_save_with_no_answer_does_not_stop_the_reset_and_is_recorded_as_unknown_never_as_not_saved()
    {
        using var run = new CloudPasswordResetPageRun(Forest(out _))
        {
            SaveAnswer = () => throw new HttpRequestException("The connection was reset."),
        };

        run.Preflight();
        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        Assert.Equal(2, run.Writes.Count);
        Assert.Single(run.OwnerMails);

        var saved = run.AuditRows().First();
        Assert.Equal("CloudPasswordReset_EmployeeIdSet", Text(saved, "action"));
        Assert.Equal("WriteIndeterminate", Text(saved, "employeeIdFill"));

        var saveAlert = Assert.Single(run.AdminAlerts(SaveAlert));
        Assert.False(saveAlert.Success);
        Assert.Equal("WriteIndeterminate", saveAlert.Details["Outcome"]);
        Assert.Contains("not known", saveAlert.Details["Result"], StringComparison.Ordinal);
        Assert.DoesNotContain("not saved", saveAlert.Details["Result"], StringComparison.Ordinal);
    }

    // ----- AC-F4: the owner's failure mode, and the reveal beside it -----

    [Fact]
    public void Someone_who_can_open_the_module_but_cannot_see_passwords_gets_todays_refusal_word_for_word_when_the_lookup_fails()
    {
        using var run = new CloudPasswordResetPageRun(NobodyForest()) { CanSeePasswords = false };

        run.Preflight();
        Assert.Equal(EmployeeIdFillOutcome.OwnerNotFound, run.Destination!.Fill!.Outcome);

        // A direct invocation, as the disabled button cannot stop: refused at the server-side gate.
        run.Confirm();

        Assert.False(run.ResultSuccess);
        Assert.Equal(RevealGateRefusal, run.ResultMessage);
        Assert.Empty(run.Writes);
        Assert.Empty(run.OwnerMails);

        var row = Assert.Single(run.AuditRows());
        Assert.Equal("CloudPasswordReset_Execute", Text(row, "action"));
        Assert.Equal("Failed", Text(row, "result"));
        Assert.Equal("DestinationNoEmployeeId", Text(row, "refusalReason"));
        Assert.Equal("OwnerNotFound", Text(row, "employeeIdFill"));
        Assert.Equal("n/a", Text(row, "employeeIdFillValue"));
        Assert.Equal("n/a", Text(row, "employeeIdFillSource"));
        Assert.Equal("n/a", Text(row, "destinationEmployeeId"));
        Assert.Equal("NotAttempted", Text(row, "passwordDelivery"));

        var alert = Assert.Single(run.AdminAlerts(ResetAlert));
        Assert.False(alert.Success);
        Assert.Equal("DestinationNoEmployeeId", alert.Details["Refused because"]);
        Assert.Equal("OwnerNotFound", alert.Details["Employee ID filled in"]);
        Assert.Empty(run.AdminAlerts(SaveAlert));
    }

    [Fact]
    public void Someone_who_can_see_passwords_gets_todays_reveal_and_nothing_is_saved_when_the_lookup_fails()
    {
        using var run = new CloudPasswordResetPageRun(NobodyForest()) { CanSeePasswords = true };

        run.Preflight();
        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        Assert.NotNull(run.RevealedPassword);
        Assert.Equal(run.WrittenPassword, run.RevealedPassword);

        // The password write and nothing else: no number was saved.
        var write = Assert.Single(run.Writes);
        Assert.Contains("\"passwordProfile\"", write.Body, StringComparison.Ordinal);
        Assert.Empty(run.OwnerMails);

        var row = Assert.Single(run.AuditRows());
        Assert.Equal("CloudPasswordReset_Revealed", Text(row, "action"));
        Assert.Equal("DestinationNoEmployeeId", Text(row, "refusalReason"));
        Assert.Equal("OwnerNotFound", Text(row, "employeeIdFill"));
        Assert.Equal("Revealed", Text(row, "passwordDelivery"));
        Assert.Empty(run.AdminAlerts(SaveAlert));
        Assert.Contains("no employee ID to save", ResetActivity(run).Steps);
    }

    // ----- AC-F6: authorization is re-checked immediately before the save -----

    [Fact]
    public void Losing_the_reset_permission_before_the_save_refuses_and_writes_nothing()
    {
        using var run = new CloudPasswordResetPageRun(Forest(out _)) { ResetChecksAllowed = 1 };

        run.Preflight();
        run.Confirm();

        Assert.False(run.ResultSuccess);
        Assert.Equal("You are not authorized to reset cloud passwords.", run.ResultMessage);
        Assert.Empty(run.Writes);
        Assert.Empty(run.OwnerMails);
        Assert.Empty(run.AdminAlerts(SaveAlert));

        var row = Assert.Single(run.AuditRows());
        Assert.Equal("CloudPasswordReset_Execute", Text(row, "action"));
        Assert.Equal("PermissionDenied", Text(row, "refusalReason"));
        Assert.Equal("Planned", Text(row, "employeeIdFill"));
        Assert.Equal(Number, Text(row, "employeeIdFillValue"));
    }

    // ----- T18 at run time: a different number to save stops the reset -----

    [Fact]
    public void A_number_that_changed_while_the_operator_was_confirming_stops_the_reset()
    {
        var forest = Forest(out var owner);
        using var run = new CloudPasswordResetPageRun(forest);
        run.Preflight();

        // Between the preflight and the confirm the owner's directory account is re-stamped. The address is unchanged;
        // the number the confirm prompt named is not the one that would now be saved.
        forest.Remove(owner);
        forest.Add(owner with { EmployeeId = "0007777" });

        run.Confirm();

        Assert.False(run.ResultSuccess);
        Assert.StartsWith("The account changed while you were confirming", run.ResultMessage, StringComparison.Ordinal);
        Assert.Empty(run.Writes);

        var row = Assert.Single(run.AuditRows());
        Assert.Equal("Planned", Text(row, "employeeIdFill"));
        Assert.Equal("0007777", Text(row, "employeeIdFillValue"));
    }

    // ----- AC-F1 and AC-F2 at run time -----

    [Fact]
    public void An_account_that_carries_a_number_takes_the_ordinary_path_and_every_record_says_NotNeeded()
    {
        using var run = new CloudPasswordResetPageRun(Forest(out _)) { AccountEmployeeId = Number };

        run.Preflight();
        Assert.Equal(EmployeeIdFillOutcome.NotNeeded, run.Destination!.Fill!.Outcome);
        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        var write = Assert.Single(run.Writes);
        Assert.DoesNotContain("employeeId", write.Body, StringComparison.Ordinal);
        Assert.Empty(run.AdminAlerts(SaveAlert));

        var row = Assert.Single(run.AuditRows());
        Assert.Equal("Sent", Text(row, "passwordDelivery"));
        Assert.Equal("NotNeeded", Text(row, "employeeIdFill"));
        Assert.Equal("n/a", Text(row, "employeeIdFillValue"));
        Assert.Equal("n/a", Text(row, "employeeIdFillSource"));
        Assert.Contains("employee ID already set", ResetActivity(run).Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_the_fill_in_off_an_account_without_a_number_is_handled_as_before_and_says_Off(bool canSeePasswords)
    {
        var forest = Forest(out _);
        var off = new Dictionary<string, string> { [CloudPasswordResetService.OwnerNameEndingsKey] = "-adm" };
        using var run = new CloudPasswordResetPageRun(forest, off) { CanSeePasswords = canSeePasswords };

        run.Preflight();
        Assert.Equal(EmployeeIdFillOutcome.Off, run.Destination!.Fill!.Outcome);
        Assert.Empty(forest.Commands);
        run.Confirm();

        var row = Assert.Single(run.AuditRows());
        Assert.Equal("Off", Text(row, "employeeIdFill"));
        Assert.Equal("DestinationNoEmployeeId", Text(row, "refusalReason"));
        if (canSeePasswords)
        {
            Assert.Equal("CloudPasswordReset_Revealed", Text(row, "action"));
            Assert.Single(run.Writes);
        }
        else
        {
            Assert.Equal(RevealGateRefusal, run.ResultMessage);
            Assert.Empty(run.Writes);
        }
    }

    [Fact]
    public void A_refusal_before_the_lookup_records_NotAttempted_whatever_the_preflight_planned()
    {
        using var run = new CloudPasswordResetPageRun(Forest(out _));
        run.Backend.Tickets.ValidateAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult(new TicketGateResult(TicketGateOutcome.Rejected, "A ticket number is required.")));

        run.Preflight();
        Assert.Equal(EmployeeIdFillOutcome.Planned, run.Destination!.Fill!.Outcome);
        run.Confirm();

        var row = Assert.Single(run.AuditRows());
        Assert.Equal("TicketInvalid", Text(row, "refusalReason"));
        Assert.Equal("NotAttempted", Text(row, "employeeIdFill"));
        Assert.Equal("n/a", Text(row, "employeeIdFillValue"));
        Assert.Empty(run.Writes);
    }

    // ----- T19 and AC-F11: one key set on every event, the closed lists, no password -----

    private static List<JsonElement> RowsOf(bool ownerExists, Action<CloudPasswordResetPageRun> arrange)
    {
        var forest = ownerExists ? Forest(out _) : NobodyForest();
        using var run = new CloudPasswordResetPageRun(forest);
        arrange(run);
        run.Preflight();
        run.Confirm();
        return run.AuditRows();
    }

    private static readonly string[] EveryEventKeys =
    [
        "targetObjectId", "targetCloudOnly", "targetDirectoryRoles", "destinationAddress", "destinationEmployeeId",
        "forceChangePasswordNextSignIn", "passwordDelivery", "revealUsed", "refusalReason", "protectedPrincipalServiced",
        "employeeIdFill", "employeeIdFillValue", "employeeIdFillSource",
    ];

    [Fact]
    public void Every_event_carries_one_key_set_with_the_three_fields_and_their_values_on_the_closed_lists()
    {
        var rows = new List<JsonElement>();
        rows.AddRange(RowsOf(ownerExists: true, _ => { }));
        rows.AddRange(RowsOf(ownerExists: true, run => run.SaveAnswer = () => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        rows.AddRange(RowsOf(ownerExists: false, run => run.CanSeePasswords = false));
        rows.AddRange(RowsOf(ownerExists: false, run => run.CanSeePasswords = true));
        rows.AddRange(RowsOf(ownerExists: true, run => run.AccountEmployeeId = Number));
        rows.AddRange(RowsOf(ownerExists: true, run => run.Backend.Tickets.ValidateAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult(new TicketGateResult(TicketGateOutcome.Rejected, "A ticket number is required.")))));

        // A save success and the reset, a save failure and the reset, a refusal and a reveal after a failed lookup, an
        // account with a number, a refusal before the lookup.
        Assert.Equal(8, rows.Count);

        foreach (var row in rows)
        {
            foreach (var key in EveryEventKeys)
                Assert.True(row.TryGetProperty(key, out _), $"{Text(row, "action")} has no {key}");
        }

        // "error" is the one key allowed to vary: AuditService nulls it on a success and the writer drops nulls
        // (CloudPasswordResetAuditShapeTests). Everything else is identical on every event.
        var keySets = rows
            .Select(row => string.Join(",", row.EnumerateObject().Select(p => p.Name).Where(n => n != "error").Order(StringComparer.Ordinal)))
            .Distinct()
            .ToList();
        Assert.True(keySets.Count == 1, "The module's events carry different key sets:\n  " + string.Join("\n  ", keySets));

        var closedList = Enum.GetNames<EmployeeIdFillOutcome>();
        Assert.All(rows, row => Assert.Contains(Text(row, "employeeIdFill"), closedList));
        Assert.Equal(
            ["NotAttempted", "NotNeeded", "OwnerNotFound", "WriteRejected", "Written"],
            rows.Select(row => Text(row, "employeeIdFill")!).Distinct().Order(StringComparer.Ordinal));

        foreach (var saved in rows.Where(row => Text(row, "action") == "CloudPasswordReset_EmployeeIdSet"))
        {
            Assert.Equal("n/a", Text(saved, "passwordDelivery"));
            Assert.False(saved.GetProperty("revealUsed").GetBoolean());
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_password_appears_in_no_audit_row_and_no_administrator_alert(bool ownerExists, bool canSeePasswords)
    {
        using var run = new CloudPasswordResetPageRun(ownerExists ? Forest(out _) : NobodyForest()) { CanSeePasswords = canSeePasswords };

        run.Preflight();
        run.Confirm();

        var password = run.WrittenPassword;
        Assert.False(string.IsNullOrEmpty(password));
        Assert.DoesNotContain(password, run.AuditText(), StringComparison.Ordinal);

        var alertText = string.Join('\n', run.Email.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync))
            .SelectMany(call => call.GetArguments())
            .SelectMany(argument => argument is IReadOnlyDictionary<string, string> details
                ? details.SelectMany(kv => new[] { kv.Key, kv.Value })
                : [argument?.ToString() ?? ""]));
        Assert.NotEmpty(alertText);
        Assert.DoesNotContain(password, alertText, StringComparison.Ordinal);
    }
}

/// <summary>
/// The confirm flow's shape (docs/CloudPasswordResetEmployeeId-Plan.md S3: T13-T18, T20), by source text, beside the
/// run-time tests above. Extends CloudPasswordResetWritePathTests' guards to the save.
/// </summary>
public class CloudPasswordResetFillPageSourceTests
{
    private static string Page() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "CloudPasswordReset.razor"));

    /// <summary>The source with its comment lines dropped, so a guard reads what the code does, not what it says.</summary>
    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Handler() =>
        AuditCategoryFilingTests.MethodBody(CodeOnly(Page()), "private async Task ExecuteResetAsync()");

    private static int IndexOf(string haystack, string needle)
    {
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{needle}' not found - this guard is pinned to code that no longer exists.");
        return i;
    }

    private const string Save = "ResetService.SetEmployeeIdAsync(";
    private const string PasswordWrite = "ResetService.ResetPasswordAsync(";

    [Fact]
    public void T13_every_refusable_gate_precedes_the_save_and_the_save_precedes_the_password_write()
    {
        var body = Handler();
        var save = IndexOf(body, Save);

        Assert.True(save < IndexOf(body, PasswordWrite), "The employee-ID save must come before the password write.");

        foreach (var gate in new[]
                 {
                     "AuthorizeAsync(authState.User, \"CloudPasswordReset\")",
                     "ValidateTicketAsync(ticket)",
                     "CheckProtectionAsync(",
                     "DeriveDestination(fresh)",
                     "AuthorizeAsync(authState.User, \"CloudPasswordResetReveal\")",
                     "Email.UserNotificationsEnabled",
                 })
        {
            Assert.True(IndexOf(body, gate) < save, $"{gate} must run before the employee-ID save.");
        }
    }

    [Fact]
    public void T14_authorization_is_rechecked_between_the_last_gate_and_the_save()
    {
        var body = Handler();
        var between = body[IndexOf(body, "Email.UserNotificationsEnabled")..IndexOf(body, Save)];

        Assert.Contains("AuthorizeAsync(authState.User, \"CloudPasswordReset\")", between, StringComparison.Ordinal);
        Assert.Contains("CloudPasswordResetRefusal.PermissionDenied", between, StringComparison.Ordinal);
    }

    [Fact]
    public void T15_nothing_between_the_save_and_the_password_write_refuses_returns_or_throws()
    {
        // J2: the reset goes on whatever the save returned.
        var body = Handler();
        var between = body[IndexOf(body, Save)..IndexOf(body, PasswordWrite)];

        Assert.DoesNotContain("RefuseAsync(", between, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\breturn\b"), between);
        Assert.DoesNotMatch(new Regex(@"\bthrow\b"), between);
    }

    [Fact]
    public void T16_the_save_is_reached_only_on_the_planned_branch_with_the_fresh_reads_object_id()
    {
        var page = CodeOnly(Page());
        var body = Handler();

        Assert.Single(Regex.Matches(page, Regex.Escape(Save)));
        Assert.Contains("ResetService.SetEmployeeIdAsync(fresh.ObjectId, plannedFill.EmployeeId!)", body, StringComparison.Ordinal);

        var branch = AuditCategoryFilingTests.MethodBody(body, "if (plannedFill is not null)");
        Assert.Contains(Save, branch, StringComparison.Ordinal);

        // The plan comes from the fresh derive, and only a Planned fill-in is one.
        Assert.True(IndexOf(body, "DeriveDestination(fresh)") < IndexOf(body, "var plannedFill = PlannedFill(destination);"));
        Assert.Contains("planned?.Fill is { Outcome: EmployeeIdFillOutcome.Planned } fill ? fill : null", page, StringComparison.Ordinal);
    }

    [Fact]
    public void T17_the_existing_refusal_texts_are_byte_identical()
    {
        var page = Page();

        Assert.Contains(
            "await RefuseAsync(upn, ticket, destination.Refusal,\n"
            + "                    \"The password could not be delivered to an owner, and you do not hold the permission to see it on screen.\");",
            page, StringComparison.Ordinal);
        Assert.Contains("? \"No owner mailbox could be determined.\"", page, StringComparison.Ordinal);
        Assert.Contains(": \"The password cannot be delivered, and you do not hold the permission to see it on screen.\";", page, StringComparison.Ordinal);
        Assert.Contains(
            "CloudPasswordResetRefusal.DestinationNoEmployeeId =>\n"
            + "            \"No employee ID is set on this account, so its owner cannot be identified.\",",
            page, StringComparison.Ordinal);
    }

    [Fact]
    public void T18_the_confirmed_plan_comparison_includes_the_number_to_be_saved()
    {
        var body = Handler();

        // Captured from what the operator confirmed, before the fresh read...
        Assert.True(
            IndexOf(body, "var confirmedFillNumber = PlannedFill(destination)?.EmployeeId;")
            < IndexOf(body, "await ResetService.ResolveTargetAsync(upn)"));

        // ...and compared in the stop, which runs before any write.
        var stop = IndexOf(body, "revealing != confirmedRevealing");
        var condition = body[stop..(stop + body[stop..].IndexOf('{'))];
        Assert.Contains("plannedFill?.EmployeeId, confirmedFillNumber", condition, StringComparison.Ordinal);
        Assert.True(stop < IndexOf(body, Save));
    }

    [Fact]
    public void T20_the_save_is_audited_and_alerted_on_every_outcome_and_no_builder_receives_the_password()
    {
        var body = Handler();
        var branch = AuditCategoryFilingTests.MethodBody(body, "if (plannedFill is not null)");
        var afterSave = branch[IndexOf(branch, Save)..];

        // Unconditionally, on whatever the save returned.
        Assert.Contains("AuditReset(upn, ticket, \"CloudPasswordReset_EmployeeIdSet\",", afterSave, StringComparison.Ordinal);
        Assert.Contains("await NotifyEmployeeIdSaveAsync(upn, ticket, save);", afterSave, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\bif\s*\("), afterSave[..IndexOf(afterSave, "NotifyEmployeeIdSaveAsync(")]);

        var page = Page();
        foreach (var builder in new[]
                 {
                     "private void AuditReset(",
                     "private async Task NotifyAdminsAsync(",
                     "private async Task NotifyEmployeeIdSaveAsync(",
                 })
        {
            var method = AuditCategoryFilingTests.MethodBody(page, builder);
            Assert.DoesNotContain("outcome.Password", method, StringComparison.Ordinal);
            Assert.DoesNotContain("revealedPassword", method, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void T19_the_audit_builder_writes_the_three_fields_with_the_sentinel_never_a_bare_null()
    {
        var builder = AuditCategoryFilingTests.MethodBody(Page(), "private void AuditReset(");

        foreach (var key in new[] { "employeeIdFill", "employeeIdFillValue", "employeeIdFillSource" })
        {
            var line = builder.Split('\n').FirstOrDefault(l => l.Contains($"[\"{key}\"]", StringComparison.Ordinal));
            Assert.NotNull(line);
            Assert.DoesNotContain("= null", line, StringComparison.Ordinal);
            Assert.DoesNotContain(": null", line, StringComparison.Ordinal);
        }

        // One builder for every event, the save's included (module plan AC18).
        Assert.Equal(1, Page().Split("[\"employeeIdFill\"]").Length - 1);
    }
}

/// <summary>T22: the descriptor (docs/CloudPasswordResetEmployeeId-Plan.md 4.3, 6, 7).</summary>
public class CloudPasswordResetFillCatalogTests
{
    private static readonly AdminModuleDescriptor Module = new ModuleCatalog().GetById("CloudPasswordReset")!;

    // The deployment's own endings and domains, as CloudPasswordResetNeutralityTests names them.
    private static readonly Regex DeploymentNames =
        new(@"analog|winroot|[-_]cld(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void The_module_is_at_1_2_0()
    {
        Assert.Equal("1.2.0", Module.Version);
    }

    [Fact]
    public void The_switch_is_a_boolean_that_defaults_off_and_the_endings_have_no_default()
    {
        var fill = Assert.Single(Module.ConfigFields, f => f.Key == CloudPasswordResetService.FillMissingEmployeeIdKey);
        Assert.Equal(ConfigFieldType.Boolean, fill.FieldType);
        Assert.Equal("false", fill.DefaultValue);
        Assert.False(fill.Required);

        var endings = Assert.Single(Module.ConfigFields, f => f.Key == CloudPasswordResetService.OwnerNameEndingsKey);
        Assert.Equal(ConfigFieldType.Text, endings.FieldType);
        Assert.Equal("", endings.DefaultValue);
        Assert.False(endings.Required);
    }

    [Fact]
    public void The_reset_permission_says_a_reset_may_first_save_an_employee_id_and_no_new_permission_is_added()
    {
        Assert.Contains("saves", Module.MainPermission.Description, StringComparison.Ordinal);
        Assert.Contains("employee ID", Module.MainPermission.Description, StringComparison.Ordinal);

        // J4: the save rides the reset permission. Still exactly one granular permission, the reveal.
        Assert.Equal("CloudPasswordResetReveal", Assert.Single(Module.GranularPermissions).PolicyAlias);
    }

    [Fact]
    public void Every_description_says_what_a_person_can_do_and_names_nothing_of_this_deployment()
    {
        // The owner's wording rule (2026-10-27): what a person can do, never "holds X but not Y". And Invariant 7: the
        // endings are typed by whoever switches the fill-in on, never written here.
        var texts = new[] { Module.Description, Module.MainPermission.Description }
            .Concat(Module.GranularPermissions.Select(p => p.Description))
            .Concat(Module.ConfigFields.Select(f => f.Description))
            .ToList();

        foreach (var text in texts)
        {
            Assert.DoesNotMatch(new Regex(@"\bholds?\b[^.]*\bbut not\b", RegexOptions.IgnoreCase), text);
            Assert.DoesNotMatch(DeploymentNames, text);
        }

        var fill = Module.ConfigFields.Single(f => f.Key == CloudPasswordResetService.FillMissingEmployeeIdKey).Description;
        Assert.Contains("Anyone who can reset passwords here can trigger that save.", fill, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reset_activity_has_six_steps_and_the_save_sits_inside_the_window_the_progress_registry_pins()
    {
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "CloudPasswordReset.razor"));
        var body = AuditCategoryFilingTests.MethodBody(page, "private async Task ExecuteResetAsync()");

        Assert.Contains("ActivitySize.Steps(6)", body, StringComparison.Ordinal);

        // ProgressRegistry pins the derive at the opening end and the last admin alert at the closing one; the save
        // between them is held by both, as the owner's mail is.
        var derive = body.IndexOf("ResetService.DeriveDestination(", StringComparison.Ordinal);
        var save = body.IndexOf("ResetService.SetEmployeeIdAsync(", StringComparison.Ordinal);
        var write = body.IndexOf("ResetService.ResetPasswordAsync(", StringComparison.Ordinal);
        Assert.True(derive >= 0 && derive < save && save < write);
    }
}
