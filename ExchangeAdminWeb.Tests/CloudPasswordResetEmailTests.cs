using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Tests for the owner's password-reset mail body
/// (docs/CloudPasswordReset-Plan.md, "Delivery, and the ordering trap that comes with it").
/// </summary>
/// <remarks>
/// The body is extracted from the send precisely so it can be asserted without SMTP. Its closing
/// line is the only place in the system that tells an owner whether to expect a change prompt,
/// and getting it wrong in either direction produces a support ticket or a lockout.
/// </remarks>
public class CloudPasswordResetEmailTests
{
    private const string Upn = "svc-admin@example.onmicrosoft.com";
    private const string Password = "CORRECT!horse@Battery3";
    private const string Ticket = "INC0012345";

    [Fact]
    public void Body_carries_the_account_the_password_and_the_ticket()
    {
        var body = EmailService.BuildCloudPasswordResetBody(Upn, Password, Ticket, forceChangeAtNextSignIn: true);

        Assert.Contains(Upn, body);
        Assert.Contains("Battery3", body);
        Assert.Contains(Ticket, body);
    }

    [Fact]
    public void Body_never_names_the_operator_field()
    {
        // The plan is explicit that the owner's mail does not identify who performed the reset.
        var body = EmailService.BuildCloudPasswordResetBody(Upn, Password, Ticket, forceChangeAtNextSignIn: true);

        Assert.DoesNotContain("Performed by", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Operator", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Promises_a_change_prompt_only_when_the_flag_is_set()
    {
        var forced = EmailService.BuildCloudPasswordResetBody(Upn, Password, Ticket, forceChangeAtNextSignIn: true);

        Assert.Contains("asked to set a new password", forced);
        Assert.DoesNotContain("keep working until you change it", forced);
    }

    [Fact]
    public void Promises_no_change_prompt_when_the_flag_is_clear()
    {
        // The direction that locks somebody out if it is wrong: an owner told to expect a prompt
        // who does not get one just ignores it, but an owner told nothing who meets a prompt on a
        // client that cannot service it cannot sign in.
        var notForced = EmailService.BuildCloudPasswordResetBody(Upn, Password, Ticket, forceChangeAtNextSignIn: false);

        Assert.Contains("keep working until you change it", notForced);
        Assert.DoesNotContain("asked to set a new password", notForced);
    }

    [Fact]
    public void The_two_flag_states_produce_different_bodies()
    {
        var forced = EmailService.BuildCloudPasswordResetBody(Upn, Password, Ticket, true);
        var notForced = EmailService.BuildCloudPasswordResetBody(Upn, Password, Ticket, false);

        Assert.NotEqual(forced, notForced);
    }

    [Fact]
    public void Html_encodes_every_value_including_the_password()
    {
        // A generated password draws from !@#$%&*?+= and could otherwise break the markup around
        // it - or, with a crafted account name, inject into it.
        var body = EmailService.BuildCloudPasswordResetBody(
            "<script>bad</script>@example.test",
            "pass&word<tag>",
            "<b>INC1</b>",
            forceChangeAtNextSignIn: true);

        Assert.DoesNotContain("<script>", body);
        Assert.Contains("&lt;script&gt;", body);
        Assert.Contains("pass&amp;word&lt;tag&gt;", body);
        Assert.Contains("&lt;b&gt;INC1&lt;/b&gt;", body);
    }

    [Fact]
    public void Tolerates_a_blank_ticket_without_throwing()
    {
        var body = EmailService.BuildCloudPasswordResetBody(Upn, Password, "", forceChangeAtNextSignIn: true);

        Assert.Contains(Upn, body);
    }

    [Fact]
    public void Send_method_is_virtual_so_callers_can_be_tested_without_smtp()
    {
        var method = typeof(EmailService).GetMethod(nameof(EmailService.SendCloudPasswordResetAsync));

        Assert.NotNull(method);
        Assert.True(method!.IsVirtual, "SendCloudPasswordResetAsync must be virtual to be seamable in tests.");
    }

    [Fact]
    public void Send_method_reports_whether_it_sent()
    {
        // Returning void would make a suppressed send indistinguishable from a delivered one, and
        // this caller has already changed the password by the time it asks.
        var method = typeof(EmailService).GetMethod(nameof(EmailService.SendCloudPasswordResetAsync));

        Assert.Equal(typeof(Task<bool>), method!.ReturnType);
    }

    [Fact]
    public void Send_method_takes_the_force_change_flag()
    {
        // Without it the closing line cannot match what was actually done.
        var method = typeof(EmailService).GetMethod(nameof(EmailService.SendCloudPasswordResetAsync));
        var parameters = method!.GetParameters().Select(p => p.Name).ToList();

        Assert.Contains("forceChangeAtNextSignIn", parameters);
    }
}
