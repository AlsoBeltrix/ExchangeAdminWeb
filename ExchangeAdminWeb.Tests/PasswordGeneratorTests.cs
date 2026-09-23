using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Tests for <see cref="PasswordGenerator"/> (docs/CloudPasswordReset-Plan.md, S2).
/// </summary>
/// <remarks>
/// Every property here is checked over many draws rather than one. A generator does not fail by
/// crashing; it fails by emitting something weaker than intended, and a single sample cannot tell
/// the difference between "correct" and "correct this time".
/// </remarks>
public class PasswordGeneratorTests
{
    /// <summary>
    /// Enough draws to make a one-in-a-hundred defect a near-certain failure, while keeping the
    /// suite fast. The generator does up to 100 internal attempts per call.
    /// </summary>
    private const int Draws = 2000;

    private static readonly PasswordGenerator Generator = new();

    private static List<string> Many(int count = Draws)
    {
        var results = new List<string>(count);
        for (var i = 0; i < count; i++) results.Add(Generator.Generate());
        return results;
    }

    [Fact]
    public void Generate_LengthIsAlwaysInRange()
    {
        foreach (var password in Many())
        {
            Assert.InRange(password.Length, PasswordGenerator.MinLength, PasswordGenerator.MaxLength);
        }
    }

    [Fact]
    public void Generate_AlwaysCarriesADigit()
    {
        foreach (var password in Many())
        {
            Assert.Contains(password, char.IsDigit);
        }
    }

    [Fact]
    public void Generate_AlwaysCarriesASeparatorSymbol()
    {
        foreach (var password in Many())
        {
            Assert.Contains(password, c => PasswordGenerator.Separators.Contains(c));
        }
    }

    [Fact]
    public void Generate_AlwaysCarriesBothCases()
    {
        // Three of Entra's four character classes are met by construction only if this holds.
        foreach (var password in Many())
        {
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
        }
    }

    [Fact]
    public void Generate_UsesOnlyExpectedCharacters()
    {
        // Letters, digits, the separator set, and the hyphen that four list words carry. Anything
        // else would be a character Entra has not been confirmed to accept.
        foreach (var password in Many())
        {
            foreach (var c in password)
            {
                var allowed = char.IsLetterOrDigit(c) || PasswordGenerator.Separators.Contains(c) || c == '-';
                Assert.True(allowed, $"Unexpected character '{c}' in a generated password.");
            }
        }
    }

    [Fact]
    public void Generate_DoesNotAlwaysEndInPadding()
    {
        // The padding distribution exists so passwords do not all end in a numeric tail. If the
        // slot seeding regressed to "append the remainder", this is what would catch it.
        var endsWithPadding = Many().Count(p =>
            char.IsDigit(p[^1]) || PasswordGenerator.Separators.Contains(p[^1]));

        Assert.True(endsWithPadding < Draws,
            "Every password ended in a digit or symbol, so padding is not being distributed across slots.");
    }

    [Fact]
    public void Generate_DoesNotAlwaysStartWithALetter()
    {
        // The mirror of the tail check: padding must be able to land before the first word too.
        var startsWithPadding = Many().Count(p =>
            char.IsDigit(p[0]) || PasswordGenerator.Separators.Contains(p[0]));

        Assert.True(startsWithPadding > 0,
            "No password began with padding, so the leading slot is never being seeded.");
    }

    [Fact]
    public void Generate_ProducesNoDuplicatesAcrossManyDraws()
    {
        var passwords = Many();
        Assert.Equal(passwords.Count, passwords.Distinct().Count());
    }

    [Fact]
    public void Generate_DoesNotRepeatAWordWithinOnePassword()
    {
        // Words are drawn without replacement. A repeat would mean the same word contributes
        // entropy twice in the score while contributing far less in reality.
        foreach (var password in Many(500))
        {
            var words = SplitIntoWords(password);
            var lowered = words.Select(w => w.ToLowerInvariant()).ToList();
            Assert.Equal(lowered.Count, lowered.Distinct().Count());
        }
    }

    [Fact]
    public void Generate_NeverPutsTwoIdenticallyStyledWordsSideBySide()
    {
        // A GUARANTEE, not a tendency. It was written as a tendency because the re-shuffle gives
        // up after 20 attempts - but AC14 states the property absolutely, and the generator now
        // has a deterministic repair pass behind the shuffles (review finding cpr-13). Asserting
        // a tolerance would have kept passing while the guarantee was broken.
        var offenders = 0;
        foreach (var password in Many(500))
        {
            var styles = SplitIntoWords(password)
                .Where(w => w.Length > 1)
                .Select(StyleOf)
                .ToList();

            for (var i = 0; i < styles.Count - 1; i++)
            {
                if (styles[i] == styles[i + 1] && styles[i] != WordStyle.Other) offenders++;
            }
        }

        Assert.True(offenders == 0,
            $"{offenders} adjacent same-style word pairs across 500 passwords. AC14 states this as a guarantee.");
    }

    [Fact]
    public void WordList_IsTheMeasuredPool()
    {
        // Pinned against Resources/CloudPasswordWordList.provenance.md. The entropy floor is
        // scored against this pool, so a silent replacement is a silent weakening.
        Assert.Equal(7771, PasswordGenerator.WordListCount);
    }

    [Fact]
    public void Generator_DoesNotReferenceSystemRandom()
    {
        // A reviewer cannot see the difference between Random and RandomNumberGenerator in a
        // diff's shape, only in its text. Every draw must come from the CSPRNG.
        var source = ReadGeneratorSource();

        Assert.DoesNotContain("System.Random", source);
        Assert.DoesNotContain("new Random(", source);
        Assert.Contains("RandomNumberGenerator", source);
    }

    [Fact]
    public void Generator_DoesNotLogOrAuditThePassword()
    {
        // The password reaches the PATCH body and the owner's mailbox and nowhere else. A logger
        // in this file is the easiest way for that to stop being true.
        var source = ReadGeneratorSource();

        Assert.DoesNotContain("ILogger", source);
        Assert.DoesNotContain("Console.", source);
    }

    [Fact]
    public void Constants_MatchThePlan()
    {
        // Fixed parameters, deliberately not config. An operator who can widen these weakens
        // every password the module issues.
        Assert.Equal(18, PasswordGenerator.MinLength);
        Assert.Equal(32, PasswordGenerator.MaxLength);
        Assert.Equal(60.0, PasswordGenerator.MinEntropyBits);
        Assert.Equal(100, PasswordGenerator.MaxAttempts);
        Assert.Equal("!@#$%&*?+=", PasswordGenerator.Separators);
    }

    private enum WordStyle { Upper, Lower, Title, Other }

    private static WordStyle StyleOf(string word)
    {
        if (word.All(c => !char.IsLetter(c) || char.IsUpper(c))) return WordStyle.Upper;
        if (word.All(c => !char.IsLetter(c) || char.IsLower(c))) return WordStyle.Lower;
        if (char.IsUpper(word[0]) && word[1..].All(c => !char.IsLetter(c) || char.IsLower(c))) return WordStyle.Title;
        return WordStyle.Other;
    }

    /// <summary>
    /// Split a password back into its word runs by removing digits and separator symbols.
    /// </summary>
    /// <remarks>
    /// The hyphen is kept as a word character, because four list words contain one and splitting
    /// on it would report them as two words and skew both the duplicate and style checks.
    /// </remarks>
    private static List<string> SplitIntoWords(string password)
    {
        var separators = PasswordGenerator.Separators.ToCharArray().Concat("0123456789").ToArray();
        return password
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Any(char.IsLetter))
            .ToList();
    }

    /// <summary>
    /// The generator source with comments removed.
    /// </summary>
    /// <remarks>
    /// The negative assertions run against CODE ONLY. The file explains in prose why it must not
    /// use System.Random, and a raw-text assertion is tripped by that explanation rather than by a
    /// regression - punishing the file for documenting itself. This happened on the first run.
    /// </remarks>
    private static string ReadGeneratorSource()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "PasswordGenerator.cs"));
        var lines = text.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join("\n", lines);
    }
}
