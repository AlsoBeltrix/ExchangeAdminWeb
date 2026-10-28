using System.Xml.Linq;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-10 (5). The test classes that read a live directory are opt-in: each carries the LiveDirectory trait, and the
/// test project's run settings filter that trait out of every run, so the default <c>dotnet test ExchangeAdminWeb.slnx</c>
/// - the mandated gate, and CI's run - makes no live directory call. The opt-in run names the other settings file on the
/// command line, which replaces the project's: <c>dotnet test ExchangeAdminWeb.slnx -s ExchangeAdminWeb.Tests/LiveDirectory.runsettings</c>.
/// </summary>
/// <remarks>
/// These tripwires hold that shape. They read the test assembly's attributes and the project's files and talk to no
/// directory, so the class is not in <see cref="LiveDirectoryCollection"/> and carries no trait.
/// </remarks>
public class LiveDirectoryOptInTests
{
    private const string TraitName = "Category";
    private const string TraitValue = "LiveDirectory";

    [Fact]
    public void EveryClassInTheLiveDirectoryCollection_CarriesTheOptInTrait_AndNoOtherClassDoes()
    {
        // The membership rule: any class that talks to a live directory joins the collection. So the collection is the
        // list the trait must cover - a class joining it without the trait would run in every default run.
        var types = typeof(LiveDirectoryOptInTests).Assembly.GetTypes();
        var inCollection = types.Where(IsInLiveDirectoryCollection).Select(t => t.FullName!).Order(StringComparer.Ordinal).ToList();
        var carryingTrait = types.Where(CarriesOptInTrait).Select(t => t.FullName!).Order(StringComparer.Ordinal).ToList();

        Assert.Contains("ExchangeAdminWeb.Tests.ADDirectoryLiveTests", inCollection);
        Assert.Contains("ExchangeAdminWeb.Tests.AdDirectoryForestSearchLiveTests", inCollection);
        Assert.Equal(inCollection, carryingTrait);
    }

    [Fact]
    public void TheProjectsRunSettings_FilterTheTraitOut_AndTheOptInSettings_SelectOnlyIt()
    {
        // Without the project's filter the trait is only a label, and the default run reads the directory again. The
        // opt-in file replaces the project's when named on the command line, so its filter is the whole run.
        var project = XDocument.Load(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.Tests", "ExchangeAdminWeb.Tests.csproj"));
        var named = project.Descendants("RunSettingsFilePath").Select(e => e.Value.Trim()).ToList();
        Assert.Equal(["$(MSBuildThisFileDirectory)ExchangeAdminWeb.Tests.runsettings"], named);

        Assert.Equal([$"{TraitName}!={TraitValue}"], Filters("ExchangeAdminWeb.Tests.runsettings"));
        Assert.Equal([$"{TraitName}={TraitValue}"], Filters("LiveDirectory.runsettings"));
    }

    private static List<string> Filters(string settingsFile) =>
        XDocument.Load(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.Tests", settingsFile))
            .Root!.Elements("RunConfiguration").Elements("TestCaseFilter").Select(e => e.Value.Trim()).ToList();

    private static bool IsInLiveDirectoryCollection(Type type) =>
        type.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(CollectionAttribute)
            && a.ConstructorArguments.Count == 1
            && Equals(a.ConstructorArguments[0].Value, LiveDirectoryCollection.Name));

    private static bool CarriesOptInTrait(Type type) =>
        type.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute)
            && a.ConstructorArguments.Count == 2
            && Equals(a.ConstructorArguments[0].Value, TraitName)
            && Equals(a.ConstructorArguments[1].Value, TraitValue));
}
