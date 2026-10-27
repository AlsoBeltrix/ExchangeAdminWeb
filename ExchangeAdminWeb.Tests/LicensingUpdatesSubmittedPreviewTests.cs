using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.LicensingUpdates;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Licensing Updates: <c>RunPreview</c> read the licence type after its paint yield, and the select reaches
/// the browser's disabled state a round trip late - so a pick landing in the yield previewed the CSV against a licence
/// type the operator had not submitted. It is now read above the yield, and a second click during a preview is ignored.
/// </summary>
/// <remarks>
/// Driven through the compiled handler over the real <see cref="LicensingUpdatesService"/>, whose first act is to refuse
/// a licence type outside its allowed list and say which type it was given - so a type none of the deployment's types
/// match makes the type that reached the service visible, before any credential, directory or protection check.
/// <see cref="SubmittedValueHarness.HeldContinuations"/> holds the handler at its yield while the test picks another.
/// </remarks>
public sealed class LicensingUpdatesSubmittedPreviewTests : IDisposable
{
    private const string TypeSubmitted = "NOT-A-TYPE-1";
    private const string TypePickedLater = "NOT-A-TYPE-2";

    private readonly TempDir _temp = new();
    private readonly RecordingProgress _progress = new();
    private readonly LicensingUpdatesService _service;

    public LicensingUpdatesSubmittedPreviewTests()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);

        // Only the module config is reached: the type check refuses before credentials, the directory, protection,
        // the trace or the audit are asked for anything.
        _service = new LicensingUpdatesService(null!, moduleConfig, null!, null!, null!, null!,
            NullLogger<LicensingUpdatesService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void APickLandingInThePreviewsYield_LeavesThePreviewOnTheLicenceTypeSubmitted()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "RunPreview"),
            () => SetField(page, "selectedLicenseType", TypePickedLater));

        var error = Field<string>(page, "previewError");
        Assert.NotNull(error);
        Assert.StartsWith($"Invalid license type '{TypeSubmitted}'.", error, StringComparison.Ordinal);
        var handle = Assert.Single(_progress.Handles);
        Assert.False(handle.Outcome?.Success);
        Assert.StartsWith($"Invalid license type '{TypeSubmitted}'.", handle.Outcome?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondClickWhileAPreviewRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "RunPreview"), () =>
        {
            // A click the browser sent before the disabled Preview rendered.
            var second = Call(page, "RunPreview");
            Assert.True(second.IsCompleted, "a second click during a preview started a second preview");
        });

        Assert.Single(_progress.Handles);
    }

    private Page PageFor()
    {
        var page = new Page();
        Set(page, "LicenseService", _service);
        Set(page, "Progress", _progress);
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "selectedFile", new CsvFile());
        SetField(page, "selectedLicenseType", TypeSubmitted);
        return page;
    }

    /// <summary>The CSV the operator picked. Never read: the type check refuses first.</summary>
    private sealed class CsvFile : IBrowserFile
    {
        public string Name => "users.csv";

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public long Size => 0;

        public string ContentType => "text/csv";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream();
    }
}
