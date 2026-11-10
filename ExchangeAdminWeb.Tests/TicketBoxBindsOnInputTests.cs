using System.Reflection;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The boxes that gate a button answer each keystroke. Each test executes the page's compiled render tree, fires
/// the box's own input-event handler as a browser does while the operator types - no blur, no change event - and
/// renders again: the button the box gates is enabled.
/// </summary>
/// <remarks>
/// No browser and no component lifecycle: the page is attached to a renderer that draws nothing, so the handler's
/// re-render request is accepted, and the markup is read by building the page's tree directly.
/// </remarks>
#pragma warning disable BL0006 // the pages' compiled render trees are read directly
public sealed class TicketBoxBindsOnInputTests
{
    [Fact]
    public async Task EventLogUndo_TypingTheTicket_EnablesConfirmUndo()
    {
        var page = new QuietEventLog();
        Set(page, "authChecked", true);
        Set(page, "undoPreview", new UndoPreview(true, null, "Module", "Action", "Target", null, null, false, null));
        using var renderer = Attach(page);

        Assert.True(Button(page.Markup, "Confirm Undo").Disabled);

        await Type(renderer, page.Markup, "Required", "INC0042");

        Assert.False(Button(page.Markup, "Confirm Undo").Disabled);
    }

    [Fact]
    public async Task MigrationSingle_TypingTheAddressAndTheTicket_EnablesCheckEligibility()
    {
        var page = new QuietMigration();
        using var renderer = Attach(page);

        Assert.True(Button(page.Markup, "Check Eligibility").Disabled);

        await Type(renderer, page.Markup, "user@example.com", "alice@contoso.test");
        Assert.True(Button(page.Markup, "Check Eligibility").Disabled);

        await Type(renderer, page.Markup, "INC0000000", "INC0042");

        Assert.False(Button(page.Markup, "Check Eligibility").Disabled);
    }

    [Fact]
    public async Task MigrationBulk_TypingTheTicket_EnablesCheckAll()
    {
        var page = new QuietMigration();
        Set(page, "tabIndex", 1);
        Set(page, "csvFile", Substitute.For<IBrowserFile>());
        using var renderer = Attach(page);

        Assert.True(Button(page.Markup, "Check All").Disabled);

        await Type(renderer, page.Markup, "INC0000000", "INC0042");

        Assert.False(Button(page.Markup, "Check All").Disabled);
    }

    [Fact]
    public async Task NamedLocations_TypingTheTicket_EnablesCreate()
    {
        using var temp = new TempDir();
        var page = new QuietNamedLocations();
        typeof(NamedLocations).GetProperty("LocationService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, ConfiguredLocationService(temp.Path));
        Set(page, "authChecked", true);
        Set(page, "showForm", true);
        Set(page, "formDisplayName", "Office");
        using var renderer = Attach(page);

        Assert.True(Button(page.Markup, "Create").Disabled);

        await Type(renderer, page.Markup, "INC/REQ number", "INC0042");

        Assert.False(Button(page.Markup, "Create").Disabled);
    }

    // The page draws its form only when the module has a Graph secret configured; nothing here calls Graph.
    private static NamedLocationsService ConfiguredLocationService(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = root,
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(root);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(root), NullLogger<ModuleConfigService>.Instance);
        moduleConfig.SaveModuleConfig("NamedLocations", new Dictionary<string, string> { ["GraphDelineaSecretId"] = "1" });
        var http = Substitute.For<IHttpClientFactory>();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);
        var extendedLog = new ExtendedLogService(
            config, env, TestConfigStore.CreateAppSettings(root), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance, extendedLog, trace);
        return new NamedLocationsService(NullLogger<NamedLocationsService>.Instance, moduleConfig, delinea, http);
    }

    // ---- the pages, drawing nothing for the renderer and their real markup on request ----

    private sealed class QuietEventLog : AdminEventLog
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        public void Markup(RenderTreeBuilder builder) => base.BuildRenderTree(builder);
    }

    private sealed class QuietMigration : Migration
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        public void Markup(RenderTreeBuilder builder) => base.BuildRenderTree(builder);
    }

    private sealed class QuietNamedLocations : NamedLocations
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        public void Markup(RenderTreeBuilder builder) => base.BuildRenderTree(builder);
    }

    private sealed class QuietRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public void Attach(IComponent component) => AssignRootComponentId(component);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }

    private static QuietRenderer Attach(IComponent page)
    {
        var renderer = new QuietRenderer();
        renderer.Attach(page);
        return renderer;
    }

    // ---- reading the tree ----

    private sealed record Element(string Name, Dictionary<string, object?> Attributes, string Text)
    {
        public bool Disabled => Attributes.TryGetValue("disabled", out var value) && value is true;
    }

    private static List<Element> Elements(Action<RenderTreeBuilder> markup)
    {
        using var builder = new RenderTreeBuilder();
        markup(builder);
        var frames = builder.GetFrames();
        var elements = new List<Element>();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType != RenderTreeFrameType.Element)
                continue;

            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            var text = new System.Text.StringBuilder();
            for (var j = i + 1; j < i + frame.ElementSubtreeLength; j++)
            {
                var inner = frames.Array[j];
                if (inner.FrameType == RenderTreeFrameType.Attribute && j == i + 1 + attributes.Count)
                    attributes[inner.AttributeName] = inner.AttributeValue;
                else if (inner.FrameType == RenderTreeFrameType.Text)
                    text.Append(inner.TextContent);
                else if (inner.FrameType == RenderTreeFrameType.Markup)
                    text.Append(inner.MarkupContent);
            }
            elements.Add(new Element(frame.ElementName, attributes, text.ToString().Trim()));
        }
        return elements;
    }

    private static Element Button(Action<RenderTreeBuilder> markup, string label) =>
        Assert.Single(Elements(markup), e => e.Name == "button" && e.Text == label);

    /// <summary>
    /// Fires the input event of the one box with this placeholder, carrying the text typed so far. A box that
    /// listens only for the change event has no such handler, and the typing reaches nothing until it loses focus.
    /// </summary>
    private static async Task Type(QuietRenderer renderer, Action<RenderTreeBuilder> markup, string placeholder, string text)
    {
        var box = Assert.Single(Elements(markup),
            e => e.Name == "input" && e.Attributes.TryGetValue("placeholder", out var p) && (string?)p == placeholder);
        Assert.True(box.Attributes.ContainsKey("oninput"), $"the '{placeholder}' box has no input-event handler");
        var handler = Assert.IsType<EventCallback>(box.Attributes["oninput"]);
        await renderer.Dispatcher.InvokeAsync(() => handler.InvokeAsync(new ChangeEventArgs { Value = text }));
    }

    private static void Set(object page, string field, object? value)
    {
        for (var type = page.GetType(); type != null; type = type.BaseType)
        {
            var info = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (info != null)
            {
                info.SetValue(page, value);
                return;
            }
        }
        throw new InvalidOperationException($"No field '{field}' on {page.GetType().Name}.");
    }
}
#pragma warning restore BL0006
