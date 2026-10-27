using System.Reflection;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a70. <see cref="PermissionResult.WithMessage"/> - the copy the permission pages make of a written change's result when its
/// audit row was not written - carries every field but the message, so the change's outcome, its applied rights and the rest
/// are what the banner and the progress panel read. Every settable property is set to a value other than its default, so a
/// field the model gains later is held to the same rule (the <c>MigrationResultRewrapTests</c> shape).
/// kd-41. <see cref="PermissionResult.WithServicedNote"/> is held to the same rule from the same builder: it carries every field
/// but the serviced note. It named every field but <see cref="PermissionResult.UnverifiedTargets"/>, so a result carrying
/// unverified targets lost them the moment a servicer's note was attached.
/// </summary>
public class PermissionResultCopyTests
{
    /// <summary>The model's settable properties - every one a copy has to carry.</summary>
    private static readonly IReadOnlyList<PropertyInfo> Properties =
        typeof(PermissionResult).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToList();

    /// <summary>
    /// A result with every settable property set to a value other than its default. A property of a type this cannot value
    /// fails loudly, so a field the model gains later is held to both copies' rule instead of passing unset.
    /// </summary>
    private static PermissionResult EveryFieldSet()
    {
        var defaults = new PermissionResult();
        var source = new PermissionResult();
        foreach (var property in Properties)
        {
            object value = property.PropertyType switch
            {
                var t when t == typeof(bool) => !(bool)property.GetValue(defaults)!,
                var t when t == typeof(string) => $"value-of-{property.Name}",
                var t when t == typeof(IReadOnlyList<string>) => new[] { $"value-of-{property.Name}" },
                var t => throw new InvalidOperationException(
                    $"PermissionResult.{property.Name} is a {t}. Give this test a non-default value for it, so the copies are held to carrying it."),
            };
            property.SetValue(source, value);
        }
        return source;
    }

    /// <summary>Every property of <paramref name="source"/> but <paramref name="replaced"/> is the same on <paramref name="copy"/>.</summary>
    private static void AssertCarriesEveryFieldBut(string replaced, PermissionResult source, PermissionResult copy, string copier)
    {
        foreach (var property in Properties.Where(p => p.Name != replaced))
            Assert.True(Equals(property.GetValue(source), property.GetValue(copy)), $"{copier} dropped PermissionResult.{property.Name}.");
    }

    [Fact]
    public void WithMessage_CarriesEveryFieldButTheMessage()
    {
        var source = EveryFieldSet();

        var copy = source.WithMessage("the new message");

        Assert.Equal("the new message", copy.Message);
        AssertCarriesEveryFieldBut(nameof(PermissionResult.Message), source, copy, nameof(PermissionResult.WithMessage));
    }

    [Fact]
    public void WithServicedNote_CarriesEveryFieldButTheServicedNote()
    {
        var source = EveryFieldSet();

        var copy = source.WithServicedNote("the new note");

        Assert.NotSame(source, copy);
        Assert.Equal("the new note", copy.ServicedNote);
        AssertCarriesEveryFieldBut(nameof(PermissionResult.ServicedNote), source, copy, nameof(PermissionResult.WithServicedNote));
    }
}
