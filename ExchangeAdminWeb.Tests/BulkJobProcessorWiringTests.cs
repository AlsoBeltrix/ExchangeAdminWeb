using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services.Jobs;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Every bulk-job processor must be wired into BOTH places or its jobs queue and never run.
/// </summary>
/// <remarks>
/// <para>
/// A processor needs two separate registrations in Program.cs: an entry in the module-id to type
/// map the runner resolves by, and an AddScoped so the DI container can construct it. Miss either
/// and the job is accepted, persisted, and sits at Queued forever.
/// </para>
/// <para>
/// <b>That failure looks like a hang, not a wiring error.</b> The page reports the job submitted,
/// the operator waits, and nothing in the log says why - which is the worst shape a
/// misconfiguration can take. It also cannot be caught by any other test here: the runner is a
/// singleton started at boot, and nothing else reads these two lists together.
/// </para>
/// <para>
/// Written when the Migration report export became the third processor
/// (docs/MigrationInterfaceRedesign-Plan.md S7). Two registrations for one processor is exactly
/// the shape that gets half-done by the next person, including a later me.
/// </para>
/// </remarks>
public class BulkJobProcessorWiringTests
{
    private static string ReadProgram()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "Program.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate Program.cs from the test base directory.");
    }

    private static List<Type> Processors() =>
        typeof(IBulkJobProcessor).Assembly
            .GetTypes()
            .Where(t => typeof(IBulkJobProcessor).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryProcessorIsBothMappedAndConstructible()
    {
        // Discovered by reflection rather than listed, so a processor added later is covered
        // without anyone remembering to extend this test.
        var processors = Processors();
        Assert.True(processors.Count >= 3, $"expected every processor to be found, got {processors.Count}");

        var program = ReadProgram();

        foreach (var type in processors)
        {
            Assert.True(
                Regex.IsMatch(program, $@"typeof\(.*{Regex.Escape(type.Name)}\)"),
                $"{type.Name} is not in the module-id map, so the runner cannot resolve a job for "
                + "it and the job sits at Queued forever");

            Assert.True(
                Regex.IsMatch(program, $@"AddScoped<.*{Regex.Escape(type.Name)}>\(\)"),
                $"{type.Name} is in the map but not in DI, so the runner finds the type and then "
                + "fails to construct it");
        }
    }

    [Fact]
    public void EveryProcessorAnnouncesTheModuleItsJobsName()
    {
        // The runner matches BulkJob.ModuleId against IBulkJobProcessor.ModuleId. A processor
        // whose declared id does not match what the submitting page writes is the same silent
        // hang, one level further in.
        foreach (var type in Processors())
        {
            var moduleName = type.GetField("ModuleName", BindingFlags.Public | BindingFlags.Static);

            Assert.True(moduleName != null,
                $"{type.Name} has no public const ModuleName for the submitting page to use, so "
                + "the page must hardcode a string and the two can drift");

            var declared = (string?)moduleName!.GetRawConstantValue();
            Assert.False(string.IsNullOrWhiteSpace(declared), $"{type.Name}.ModuleName is blank");
        }
    }

    [Fact]
    public void TheMigrationExportJobAndItsProcessorAgreeOnTheModuleId()
    {
        // The pair added in S7, asserted directly because a mismatch here is the exact failure
        // the remarks above describe and the export is the newest of the three.
        Assert.Equal(MigrationReportExportJobPayload.ModuleId, MigrationReportExportProcessor.ModuleName);
    }
}
