using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-88: every protection check logs one Information line with what it cost - "Protected-principal check for {Target} took
/// {ElapsedMs} ms ({Reads} directory reads, {Fetches} credential fetches)" - so the owner reads the duration on dev after the
/// redeploy. Driven through the real validator and protected-principal service over the stand-in forest.
/// </summary>
public sealed class ProtectionCheckTimingTests : IDisposable
{
    private const string Target = "shared@contoso.test";
    private const string Other = "other@contoso.test";
    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-5555-4666-8777-999999999999");

    private readonly ExchangeWriteHarness _h = new();
    private readonly SelfGrantDirectoryStandIn.Entry _target;
    private readonly string _protectedGroupDn;
    private readonly LogLines<ProtectedPrincipalService> _serviceLog = new();
    private readonly LogLines<PermissionValidator> _validatorLog = new();

    public ProtectionCheckTimingTests()
    {
        _h.AddMailbox(Target, TargetMailbox);
        _target = _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = $"CN=shared,OU=Staff,{SelfGrantDirectoryStandIn.DomainDn}",
            Class = "user",
            Upn = Target,
            Mail = Target,
            Sam = "shared",
            Sid = $"{SelfGrantDirectoryStandIn.DomainSid}-3997",
            PrimaryGroupId = SelfGrantDirectoryStandIn.DomainUsersRid,
            Proxies = [$"SMTP:{Target}"],
        });
        _h.GraphSids["ext-" + Target] = $"{SelfGrantDirectoryStandIn.DomainSid}-3997";
        var other = _h.Forest.AddUser("other", upn: Other, mail: Other, proxies: [$"SMTP:{Other}"]);
        _protectedGroupDn = _h.Forest.AddGroup("VIP Staff", members: [other]).Dn;
    }

    public void Dispose() => _h.Dispose();

    private PermissionValidator Validator()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);
        resolver.ResolveRecipientAsync(Target).Returns(
            new ResolvedRecipient(Target, "ext-" + Target, "UserMailbox", ExistsOnPrem: true, ExchangeGuid: TargetMailbox));
        return _h.ProtectedValidator(resolver, new ProtectedPrincipalConfig { Groups = [_protectedGroupDn] },
            protectedPrincipalsLogger: _serviceLog, validatorLogger: _validatorLog);
    }

    private ResolvedDirectoryPrincipal TargetPrincipal => new(
        "Test", Target, Target, "shared", Target, _target.Dn, _target.Guid.ToString(), null);

    private static Match Line(IEnumerable<string> lines, string check, string target)
    {
        var pattern = new Regex(
            $@"^{Regex.Escape(check)} for {Regex.Escape(target)} took (?<ms>\d+) ms \((?<reads>\d+) directory reads, (?<fetches>\d+) credential fetches(, (?<sid>\d+) SID source reads from Graph in \d+ ms)?\)$");
        var matches = lines.Select(line => pattern.Match(line)).Where(match => match.Success).ToList();
        return Assert.Single(matches);
    }

    [Fact]
    public async Task TheProtectedPrincipalCheck_LogsOneLine_WithItsReadsAndItsFetch()
    {
        Validator();

        var result = await _h.ProtectedPrincipals!.CheckAsync(TargetPrincipal);

        Assert.False(result.CheckFailed, result.Reason);
        // The target read, its token, and the one protected group's own read; one fetch.
        var line = Line(_serviceLog.Information, "Protected-principal check", Target);
        Assert.Equal("3", line.Groups["reads"].Value);
        Assert.Equal("1", line.Groups["fetches"].Value);
    }

    [Fact]
    public async Task ATargetValidation_LogsOneLine_ForItsResolutionAndItsCheck()
    {
        var validation = await Validator().ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions");

        Assert.Null(validation.Error);
        // One line for the whole validation - the service's own check inside it is part of it: the resolution's lookup, the
        // catalog's discovery (once per service) and the read by SID there, and the check's three reads; the one fetch they
        // share; and the one Graph read of the SID, not a directory read, counted on its own (docs/ProtectedPrincipalIdentifiers-Plan.md E).
        var line = Line(_serviceLog.Information.Concat(_validatorLog.Information), "Protected-principal check", Target);
        Assert.Equal("6", line.Groups["reads"].Value);
        Assert.Equal("1", line.Groups["fetches"].Value);
        Assert.Equal("1", line.Groups["sid"].Value);
        Assert.Equal(1, _h.GraphSidReads);
    }

    [Fact]
    public async Task TheSelfGrantCheck_LogsItsOwnLine_AndSharesTheChangesFetch()
    {
        var validator = Validator();
        using (DirectoryReadOperation.Begin())
        {
            Assert.Null((await validator.ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions")).Error);
            Assert.Null(await validator.ValidateSelfGrantAsync(_h.Operator, Other));
        }

        Assert.Equal("1", Line(_serviceLog.Information, "Protected-principal check", Target).Groups["fetches"].Value);
        // The catalog's discovery, the operator by SID, the grantee's lookup; no fetch of its own - the change had it.
        var selfGrant = Line(_validatorLog.Information, "Self-grant check", Other);
        Assert.Equal("3", selfGrant.Groups["reads"].Value);
        Assert.Equal("0", selfGrant.Groups["fetches"].Value);
        Assert.Equal(1, _h.DirectoryReadFetches);
    }

    [Fact]
    public async Task ACheckBegunAroundAResolution_LogsOneLine_ForBoth()
    {
        // The sweep's shape: a site begins one check around its resolution and its check.
        Validator();
        var service = _h.ProtectedPrincipals!;
        using (service.BeginCheck(Target))
        {
            var (resolved, status) = await service.ResolveExchangeTargetAsync(Target);
            Assert.Equal(ProtectedPrincipalService.ResolutionStatus.Resolved, status);
            Assert.False((await service.CheckAsync(resolved!)).CheckFailed);
        }

        var line = Line(_serviceLog.Information, "Protected-principal check", Target);
        Assert.Equal("6", line.Groups["reads"].Value);
        Assert.Equal("1", line.Groups["fetches"].Value);
        Assert.Equal("1", line.Groups["sid"].Value);

        // A second check: the catalog the first discovered is kept, so it is one read fewer.
        using (service.BeginCheck(Target))
        {
            var (again, _) = await service.ResolveExchangeTargetAsync(Target);
            Assert.False((await service.CheckAsync(again!)).CheckFailed);
        }
        var second = _serviceLog.Information.Last(l => l.StartsWith("Protected-principal check for ", StringComparison.Ordinal));
        Assert.Contains("(5 directory reads, 1 credential fetches, 1 SID source reads from Graph in ", second, StringComparison.Ordinal);
    }

    /// <summary>A logger that keeps the Information lines it is given, as rendered.</summary>
    private sealed class LogLines<T> : ILogger<T>
    {
        private readonly List<string> _information = [];

        public IReadOnlyList<string> Information
        {
            get
            {
                lock (_information)
                    return _information.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Information)
                return;
            lock (_information)
                _information.Add(formatter(state, exception));
        }
    }
}
