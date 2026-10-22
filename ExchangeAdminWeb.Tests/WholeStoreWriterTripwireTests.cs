using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26k: the settings stores' whole-content writers - each replaces a store or a module's slice of
/// one with the caller's map, comparing nothing and recording nothing - had no production caller and
/// stayed public. They are the snapshot overwrite aud-a28i, aud-a28k and aud-a28n took out of the pages.
/// </summary>
/// <remarks>
/// Kept as internal test fixtures (the suite seeds through them), so two guards stand in for their
/// removal: none is public, and no production source calls one - nor reaches the two named stores'
/// whole-table <c>SaveAll</c> except from inside those fixtures.
/// </remarks>
public sealed class WholeStoreWriterTripwireTests
{
    private static readonly (Type Type, string Method)[] Writers =
    [
        (typeof(ModuleEnablementService), "SaveEnablement"),
        (typeof(SectionAccessService), "SaveSectionAccess"),
        (typeof(ModuleConfigService), "SaveModuleConfig"),
        (typeof(ModuleAdminService), "SetModuleAdmins"),
        (typeof(ProtectedPrincipalService), "SaveConfig"),
    ];

    [Theory]
    [InlineData("ModuleEnablementService.SaveEnablement")]
    [InlineData("SectionAccessService.SaveSectionAccess")]
    [InlineData("ModuleConfigService.SaveModuleConfig")]
    [InlineData("ModuleAdminService.SetModuleAdmins")]
    [InlineData("ProtectedPrincipalService.SaveConfig")]
    public void AWholeStoreWriter_IsNotPublic(string writer)
    {
        var (type, name) = Writers.Single(w => $"{w.Type.Name}.{w.Method}" == writer);

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name)
            .ToList();

        Assert.True(methods.Count > 0, $"{writer} is gone - this tripwire is stale");
        Assert.All(methods, m => Assert.True(m.IsAssembly, $"{writer} is {(m.IsPublic ? "public" : "not internal")}: only the per-row saves may be the store's public writers"));
    }

    private static readonly string[] ProductionRoots = ["Services", "Components", "Modules", "Authorization", "Middleware", "Models"];

    private static IEnumerable<(string Path, string Code)> ProductionSources()
    {
        var root = Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.csproj"))!;
        var files = ProductionRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly));

        foreach (var file in files)
            yield return (Path.GetRelativePath(root, file).Replace('\\', '/'), RazorSyntax.CommentsBlanked(File.ReadAllText(file)));
    }

    [Fact]
    public void NoProductionSource_CallsAWholeStoreWriter()
    {
        var call = new Regex(@"\.(?:" + string.Join("|", Writers.Select(w => w.Method)) + @")\s*\(");

        var callers = ProductionSources()
            .SelectMany(s => call.Matches(s.Code).Select(m => $"{s.Path}:{RazorSyntax.LineOf(s.Code, m.Index)} {m.Value}"))
            .ToList();

        Assert.True(callers.Count == 0, "A production caller of a whole-store writer: " + string.Join("; ", callers));
    }

    [Fact]
    public void TheNamedStoresWholeTableSave_IsReachedOnlyFromItsFixture()
    {
        // Nothing may bypass the per-row saves through the repository, a DI singleton any page could inject.
        var fixtures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Services/ModuleEnablementService.cs"] = "internal void SaveEnablement(",
            ["Services/SectionAccessService.cs"] = "internal void SaveSectionAccess(",
        };
        var call = new Regex(@"\.SaveAll\s*\(");

        var callers = new List<string>();
        var fixtureCalls = 0;
        foreach (var (path, code) in ProductionSources())
        {
            var scanned = code;
            if (fixtures.TryGetValue(path, out var signature))
            {
                var body = AuditCategoryFilingTests.MethodBody(code, signature);
                fixtureCalls += call.Matches(body).Count;
                scanned = code.Replace(body, new string(' ', body.Length), StringComparison.Ordinal);
            }

            callers.AddRange(call.Matches(scanned).Select(m => $"{path}:{RazorSyntax.LineOf(scanned, m.Index)}"));
        }

        Assert.True(fixtureCalls == fixtures.Count, "the fixtures no longer call SaveAll - this tripwire is stale");
        Assert.True(callers.Count == 0, "A whole-table SaveAll reached outside its fixture: " + string.Join("; ", callers));
    }
}
