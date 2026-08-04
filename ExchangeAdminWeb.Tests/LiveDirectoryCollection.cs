namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Serialises every test class that drives a REAL Active Directory.
/// </summary>
/// <remarks>
/// xUnit runs test classes in parallel by default. That is fine for pure logic and wrong here:
/// each of these classes constructs its own <c>ADDirectorySearchService</c>, and each service
/// opens a PowerShell runspace, imports the ActiveDirectory module, probes the forest, and
/// serialises its own calls behind a 30-second lock. Several at once contend for the directory,
/// and the services are fail-soft -- a throttled or failed call returns an empty list that looks
/// exactly like "no such object".
///
/// The observable cost of not doing this: a forest-scope test that passed alone and failed about
/// one full-suite run in three, with a message that read like a genuine product regression
/// ("Forest has 2 domains but group search returned only: ad.analog.com"). It was worth chasing
/// once -- it exposed a real caching defect -- but a test that fails intermittently for
/// environmental reasons teaches people to re-run CI instead of reading it.
///
/// Membership rule: any class that talks to a live directory belongs here. Pure-function tests
/// must NOT join, or the suite serialises for no reason.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveDirectoryCollection
{
    public const string Name = "LiveDirectory";
}
