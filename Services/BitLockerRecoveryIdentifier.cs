using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Services;

public enum BitLockerRecoveryIdentifierKind
{
    KeyIdPrefix,
    RecoveryPassword,
}

public readonly record struct BitLockerRecoveryIdentifier(
    BitLockerRecoveryIdentifierKind Kind,
    string Value);

public static class BitLockerRecoveryIdentifierParser
{
    private static readonly Regex RecoveryPasswordPattern = new(
        @"(?<!\d)(\d{6})[-\s]*(\d{6})[-\s]*(\d{6})[-\s]*(\d{6})[-\s]*(\d{6})[-\s]*(\d{6})[-\s]*(\d{6})[-\s]*(\d{6})(?![-\s]*\d)",
        RegexOptions.Compiled);

    private static readonly Regex FullKeyIdPattern = new(
        @"\{?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}?",
        RegexOptions.Compiled);

    private static readonly Regex ShortKeyIdPattern = new(
        @"(?<![0-9a-fA-F])([0-9a-fA-F]{8})(?![0-9a-fA-F])",
        RegexOptions.Compiled);

    public static BitLockerRecoveryIdentifier? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        var password = RecoveryPasswordPattern.Match(text);
        if (password.Success)
        {
            var parts = new string[8];
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = password.Groups[i + 1].Value;
            }

            return new BitLockerRecoveryIdentifier(
                BitLockerRecoveryIdentifierKind.RecoveryPassword,
                string.Join("-", parts));
        }

        var fullKeyId = FullKeyIdPattern.Match(text);
        if (fullKeyId.Success)
        {
            return new BitLockerRecoveryIdentifier(
                BitLockerRecoveryIdentifierKind.KeyIdPrefix,
                fullKeyId.Groups[1].Value.ToLowerInvariant());
        }

        var shortKeyId = ShortKeyIdPattern.Match(text);
        if (shortKeyId.Success)
        {
            return new BitLockerRecoveryIdentifier(
                BitLockerRecoveryIdentifierKind.KeyIdPrefix,
                shortKeyId.Groups[1].Value.ToLowerInvariant());
        }

        return null;
    }
}
