using System.Security.Claims;

namespace ExchangeAdminWeb.Models.AccountLockoutRemediation;

public sealed record AccountLockoutSourceRequest(
    string[] Users,
    int WithinHours,
    string[] DomainControllers,
    int ThrottleLimit);

public sealed record AccountLockoutLogoffRequest(
    string[] Users,
    int WithinHours,
    string[] DomainControllers,
    bool Execute,
    string TicketNumber,
    int ThrottleLimit);

public sealed record AccountScopedLogoffRequest(
    string[] Users,
    string SearchBase,
    string[] ExtraComputers,
    bool Execute,
    string TicketNumber,
    int ThrottleLimit);

public sealed record AccountLockoutEventRow(
    string User,
    string UserRaw,
    string SourceMachine,
    string DomainController,
    DateTime? TimeCreated,
    bool Actionable,
    string Detail);

public sealed record ComputerSessionActionRow(
    string Computer,
    string User,
    string SessionId,
    string State,
    string Action,
    bool Success,
    string Detail);

public sealed record AccountLockoutDiscoveryResult(
    bool Success,
    string Message,
    IReadOnlyList<AccountLockoutEventRow> Events,
    IReadOnlyList<string> ImplicatedMachines,
    IReadOnlyList<string> ReadFailures);

public sealed record AccountLogoffResult(
    bool Success,
    string Message,
    bool Executed,
    int MachineCount,
    int SessionHitCount,
    int FailureCount,
    IReadOnlyList<ComputerSessionActionRow> Rows);

public sealed record AccountLockoutOperatorContext(
    ClaimsPrincipal Principal,
    string DisplayName,
    string IpAddress);
