using System.Reflection;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The word-list validation added for review finding cpr-12.
/// </summary>
/// <remarks>
/// The original check was a count floor - "at least 1000 words" - and a list of 7,771 copies of
/// the same word passed it. Every selected word then scored against a 7,771-entry length bucket,
/// so the computed entropy cleared 60 bits while the real entropy was near zero. The generator
/// would have reported a strong password it had not produced.
///
/// These drive the validator directly, because the real path loads an embedded resource that
/// cannot be swapped at test time.
/// </remarks>
public class PasswordGeneratorWordListTests
{
    private static void Validate(List<string> words)
    {
        var method = typeof(ExchangeAdminWeb.Services.PasswordGenerator)
            .GetMethod("ValidateWordList", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        try
        {
            method!.Invoke(null, [words]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>A list that satisfies every rule, so each test can break exactly one thing.</summary>
    private static List<string> ValidList()
    {
        // Four lowercase letters, base-26 from the index: distinct, inside the 3-9 range, and no
        // digits - the first draft of this fixture used "w0001" and was correctly refused by the
        // validator it was meant to exercise.
        var words = new List<string>(7771);
        for (var i = 0; i < 7771; i++)
        {
            var chars = new char[4];
            var n = i;
            for (var p = 3; p >= 0; p--)
            {
                chars[p] = (char)('a' + n % 26);
                n /= 26;
            }
            words.Add(new string(chars));
        }
        return words;
    }

    [Fact]
    public void A_well_formed_list_passes()
    {
        Validate(ValidList());
    }

    [Fact]
    public void A_list_of_duplicates_is_refused()
    {
        // The defect in one line. This list has the right COUNT and the right SHAPE, and would
        // have passed the original floor check while making the entropy score a fiction.
        var words = Enumerable.Repeat("aaa", 7771).ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => Validate(words));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Even_a_single_duplicate_is_refused()
    {
        // Not a threshold. One duplicate means one length bucket is overstated by one, which is a
        // small lie - but the rule is that the pool is distinct, and a tolerance is a place for
        // the next person to argue the number up.
        var words = ValidList();
        words[100] = words[0];

        Assert.Throws<InvalidOperationException>(() => Validate(words));
    }

    [Fact]
    public void A_short_or_long_word_is_refused()
    {
        // The length-fitting logic assumes the documented 3-9 range; a 2- or 12-character entry
        // breaks the budget arithmetic rather than merely being unusual.
        foreach (var bad in new[] { "ab", "overlongword" })
        {
            var words = ValidList();
            words[42] = bad;

            var ex = Assert.Throws<InvalidOperationException>(() => Validate(words));
            Assert.Contains("character", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_word_outside_the_documented_character_shape_is_refused()
    {
        // Uppercase, digits, accents and symbols all mean the pool is not what the provenance
        // record measured - and an unverified character could be one Entra rejects.
        // The accented entry is written as an escape, not as a literal: this repo's CI fails on
        // any non-ASCII byte in a tracked .cs file, comments included.
        foreach (var bad in new[] { "ABC", "wo3rd", "café", "wo_rd" })
        {
            var words = ValidList();
            words[7] = bad;

            Assert.Throws<InvalidOperationException>(() => Validate(words));
        }
    }

    [Fact]
    public void A_hyphen_is_allowed_because_four_real_entries_carry_one()
    {
        // drop-down, felt-tip, t-shirt, yo-yo. Rejecting the hyphen would refuse the real list.
        var words = ValidList();
        words[3] = "yo-yo";

        Validate(words);
    }

    [Fact]
    public void A_list_of_the_wrong_size_is_refused_in_both_directions()
    {
        // Pinned, not a floor: a pool that GREW is as much a change to the measured entropy as one
        // that shrank, and both should be a deliberate edit to two files rather than a surprise.
        var tooFew = ValidList();
        tooFew.RemoveAt(0);
        Assert.Throws<InvalidOperationException>(() => Validate(tooFew));

        var tooMany = ValidList();
        tooMany.Add("zzzz");
        Assert.Throws<InvalidOperationException>(() => Validate(tooMany));
    }

    [Fact]
    public void The_refusal_message_says_what_to_do_about_it()
    {
        var tooFew = ValidList();
        tooFew.RemoveAt(0);

        var ex = Assert.Throws<InvalidOperationException>(() => Validate(tooFew));

        Assert.Contains("provenance", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_real_embedded_list_satisfies_every_rule()
    {
        // The validator runs at static initialisation, so merely touching the generator proves the
        // shipped resource passes. Stated as its own test so the coverage is visible rather than
        // implied by every other test happening to construct one.
        Assert.Equal(7771, ExchangeAdminWeb.Services.PasswordGenerator.WordListCount);
    }
}

/// <summary>
/// The deterministic adjacency repair added for review finding cpr-13.
/// </summary>
/// <remarks>
/// **Tested directly because the end-to-end test cannot catch it.** Twenty random shuffles almost
/// always produce a valid arrangement, so 500 generated passwords pass whether or not the repair
/// exists - removing it was mutation-probed and the draw-based test stayed green. A guarantee
/// asserted only by sampling a rare event is not asserted at all.
/// </remarks>
public class PasswordGeneratorStyleRepairTests
{
    private static void Repair(List<int> styles)
    {
        var method = typeof(ExchangeAdminWeb.Services.PasswordGenerator)
            .GetMethod("RepairAdjacentDuplicates", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, [styles]);
    }

    private static bool HasAdjacent(List<int> styles)
    {
        for (var i = 1; i < styles.Count; i++)
        {
            if (styles[i] == styles[i - 1]) return true;
        }
        return false;
    }

    [Theory]
    // Every multiset the generator can build, handed to the repair in its worst arrangement.
    [InlineData(new[] { 0, 0, 1 })]
    [InlineData(new[] { 0, 0, 1, 1 })]
    [InlineData(new[] { 0, 1, 1, 2 })]
    [InlineData(new[] { 0, 0, 1, 2 })]
    [InlineData(new[] { 0, 0, 1, 1, 2 })]
    [InlineData(new[] { 0, 0, 1, 1, 2, 2 })]
    [InlineData(new[] { 2, 2, 1, 1, 0, 0 })]
    [InlineData(new[] { 0, 1, 2, 0, 1, 2 })]
    public void Repair_leaves_no_two_neighbours_alike(int[] input)
    {
        var styles = input.ToList();

        Repair(styles);

        Assert.False(HasAdjacent(styles), "Repair left an adjacent duplicate: " + string.Join(",", styles));
    }

    [Theory]
    [InlineData(new[] { 0, 0, 1, 1, 2, 2 })]
    [InlineData(new[] { 0, 0, 1, 2 })]
    public void Repair_preserves_the_multiset(int[] input)
    {
        // It must rearrange, never substitute. Changing which styles are present would unbalance
        // the distribution the entropy score counts arrangements of.
        var styles = input.ToList();
        var before = input.OrderBy(x => x).ToList();

        Repair(styles);

        Assert.Equal(before, styles.OrderBy(x => x).ToList());
    }

    [Fact]
    public void Repair_leaves_an_already_valid_arrangement_alone()
    {
        // The common case: 20 random shuffles succeeded and the repair must not disturb the
        // randomness they produced.
        var styles = new List<int> { 0, 1, 2, 0, 1, 2 };
        var original = styles.ToList();

        Repair(styles);

        Assert.Equal(original, styles);
    }
}
