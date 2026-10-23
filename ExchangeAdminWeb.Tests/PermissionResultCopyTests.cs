using System.Reflection;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a70. <see cref="PermissionResult.WithMessage"/> - the copy the permission pages make of a written change's result when its
/// audit row was not written - carries every field but the message, so the change's outcome, its applied rights and the rest
/// are what the banner and the progress panel read. Every settable property is set to a value other than its default, so a
/// field the model gains later is held to the same rule (the <c>MigrationResultRewrapTests</c> shape).
/// </summary>
public class PermissionResultCopyTests
{
    [Fact]
    public void WithMessage_CarriesEveryFieldButTheMessage()
    {
        var defaults = new PermissionResult();
        var source = new PermissionResult();
        var properties = typeof(PermissionResult).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToList();
        foreach (var property in properties)
        {
            object value = property.PropertyType switch
            {
                var t when t == typeof(bool) => !(bool)property.GetValue(defaults)!,
                var t when t == typeof(string) => $"value-of-{property.Name}",
                var t when t == typeof(IReadOnlyList<string>) => new[] { $"value-of-{property.Name}" },
                var t => throw new InvalidOperationException(
                    $"PermissionResult.{property.Name} is a {t}. Give this test a non-default value for it, so WithMessage is held to carrying it."),
            };
            property.SetValue(source, value);
        }

        var copy = source.WithMessage("the new message");

        Assert.Equal("the new message", copy.Message);
        foreach (var property in properties.Where(p => p.Name != nameof(PermissionResult.Message)))
            Assert.True(Equals(property.GetValue(source), property.GetValue(copy)), $"WithMessage dropped PermissionResult.{property.Name}.");
    }
}
