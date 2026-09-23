using System.Security.Cryptography;
using System.Text;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Diceware-style passphrase generator for Cloud Password Reset.
/// </summary>
/// <remarks>
/// A C# implementation of the METHOD used by the owner's pwgen
/// (github.com/AlsoBeltrix/PasswordGenerator, MIT, owner's own repo), not a port of its source
/// and not a dependency on its binary. Owner ruling 2026-09-10, plan decision D1: the app chooses
/// the password. Algorithm and parameters: docs/CloudPasswordReset-Plan.md, "The generated
/// password".
///
/// Every parameter here is fixed and deliberately NOT a config field. An operator who can widen
/// the length range, lower the entropy floor or shrink the word list can weaken every password
/// the module ever issues, and could do it without touching code.
///
/// THE PASSWORD NEVER LEAVES THIS CLASS EXCEPT AS THE RETURN VALUE. No candidate, no rejected
/// attempt, and no length or entropy figure tied to a specific reset is logged, audited or traced.
/// There is no logger in this file on purpose.
///
/// System.Random appears nowhere here and must not: every draw - word, style, separator, digit,
/// slot and shuffle - comes from RandomNumberGenerator. A test asserts the type is not referenced
/// in this file's source text, because a reviewer cannot see the difference in a diff.
/// </remarks>
public sealed class PasswordGenerator
{
    /// <summary>Shortest password this generator will emit.</summary>
    public const int MinLength = 18;

    /// <summary>Longest password this generator will emit.</summary>
    public const int MaxLength = 32;

    /// <summary>Entropy floor in bits. Below this the attempt is discarded, never accepted.</summary>
    public const double MinEntropyBits = 60.0;

    /// <summary>Attempts before the generator refuses. It never degrades to a weaker password.</summary>
    public const int MaxAttempts = 100;

    /// <summary>
    /// Separator and padding symbol set. Confirmed against Entra's allowed password characters
    /// before first live use - see the plan's S3 note.
    /// </summary>
    public const string Separators = "!@#$%&*?+=";

    private const int StyleUpper = 0;
    private const int StyleLower = 1;
    private const int StyleTitle = 2;

    private static readonly string[] WordList = LoadWordList();
    private static readonly Dictionary<int, int> LengthCounts = BuildLengthCounts(WordList);

    /// <summary>Number of words in the embedded list. Exposed so a test can pin it.</summary>
    public static int WordListCount => WordList.Length;

    /// <summary>
    /// Generate one password, or throw if the constraints cannot be met.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown after <see cref="MaxAttempts"/> attempts that all failed the entropy floor or the
    /// length range. The caller must treat this as a failed reset. It must NOT retry with weaker
    /// parameters: refusing is the designed behaviour, not an error to work around.
    /// </exception>
    public string Generate()
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = GenerateOnce();
            if (candidate.EntropyBits >= MinEntropyBits &&
                candidate.Password.Length >= MinLength &&
                candidate.Password.Length <= MaxLength)
            {
                return candidate.Password;
            }
        }

        // Deliberately carries no detail about the attempts. The caller surfaces a failed reset;
        // nothing about the candidates is safe to put in a message that may be logged.
        throw new InvalidOperationException(
            $"Could not generate a password meeting the required strength after {MaxAttempts} attempts. No password was produced.");
    }

    private sealed record Candidate(string Password, double EntropyBits);

    private static Candidate GenerateOnce()
    {
        var targetLength = SecureRange(MinLength, MaxLength);

        var minWords = MinWordCountForEntropy(MinEntropyBits, Separators.Length);
        var maxByLength = Math.Max(2, Math.Min(6, targetLength / 4));
        var wordCount = minWords >= maxByLength ? maxByLength : SecureRange(minWords, maxByLength);

        var selected = SelectWordsForLength(wordCount, targetLength);
        var effectivePool = EffectivePool(selected);
        var styled = ApplyStyles(selected);
        var paddingCount = Math.Max(0, targetLength - BaseLength(styled));
        var password = Assemble(styled, targetLength);
        var entropy = EntropyBits(wordCount, Separators.Length, paddingCount, effectivePool);

        return new Candidate(password, entropy);
    }

    /// <summary>
    /// Smallest word count whose CONSERVATIVE entropy estimate clears the floor.
    /// </summary>
    /// <remarks>
    /// 9 bits per word is a pessimistic stand-in for a ~512-word pool per position, far below the
    /// real list size. The estimate must never over-count, or this returns a word count that
    /// cannot actually reach the floor and every attempt is then rejected downstream.
    /// </remarks>
    private static int MinWordCountForEntropy(double minEntropy, int separatorCount)
    {
        if (minEntropy <= 0.0) return 2;

        for (var n = 2; n <= 6; n++)
        {
            var wordBits = 9.0 * n;
            var sepBits = separatorCount > 1 ? Log2(separatorCount) * (n - 1) : 0.0;
            var paddingBits = Log2(10.0) + (separatorCount > 1 ? Log2(separatorCount) : 0.0);
            var styleBits = StyleEstimateBits(n);

            if (wordBits + sepBits + paddingBits + styleBits >= minEntropy) return n;
        }

        return 6;
    }

    private static double StyleEstimateBits(int wordCount) => wordCount switch
    {
        2 or 3 => 2.6,
        4 => 4.2,
        5 => 5.2,
        6 => 4.9,
        _ => 2.6,
    };

    /// <summary>
    /// Choose the words, fitted to a character budget that leaves room for separators and at
    /// least one padding character.
    /// </summary>
    /// <remarks>
    /// Ten fully random draws first, because an unconstrained draw is the least biased thing
    /// available and usually fits. Only if none does are lengths decided first and words fitted
    /// to them - and those lengths are SHUFFLED before assignment. The shuffle is load-bearing:
    /// the guided loop works the budget down from the front, so without it long words cluster at
    /// the start and the password's own shape leaks how it was built.
    /// </remarks>
    private static List<string> SelectWordsForLength(int wordCount, int targetLength)
    {
        var separatorChars = wordCount - 1;
        var budgetMax = Math.Max(0, targetLength - separatorChars - 1);
        var budgetMin = wordCount * 3;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var words = new List<string>(wordCount);
            var used = new HashSet<int>();
            while (words.Count < wordCount)
            {
                var index = SecureInt(WordList.Length);
                if (used.Add(index)) words.Add(WordList[index]);
            }

            var total = words.Sum(w => w.Length);
            if (total >= budgetMin && total <= budgetMax) return words;
        }

        var lengths = new List<int>(wordCount);
        var remainingMax = budgetMax;
        var remainingMin = budgetMin;

        for (var i = 0; i < wordCount; i++)
        {
            var wordsLeft = wordCount - i;
            var minForOthers = (wordsLeft - 1) * 3;
            var maxForOthers = (wordsLeft - 1) * 12;

            var wordMin = Math.Max(3, remainingMin - maxForOthers);
            var wordMax = Math.Min(15, Math.Max(0, remainingMax - minForOthers));
            var chosen = wordMin >= wordMax ? wordMin : SecureRange(wordMin, wordMax);

            remainingMax = Math.Max(0, remainingMax - chosen);
            remainingMin = Math.Max(0, remainingMin - chosen);
            lengths.Add(chosen);
        }

        Shuffle(lengths);

        var selected = new List<string>(wordCount);
        var usedIndices = new HashSet<int>();

        foreach (var length in lengths)
        {
            var candidates = new List<int>();
            for (var k = 0; k < WordList.Length; k++)
            {
                if (WordList[k].Length == length && !usedIndices.Contains(k)) candidates.Add(k);
            }

            if (candidates.Count == 0)
            {
                for (var k = 0; k < WordList.Length; k++)
                {
                    if (!usedIndices.Contains(k)) candidates.Add(k);
                }
            }

            if (candidates.Count == 0)
            {
                selected.Add(WordList[SecureInt(WordList.Length)]);
                continue;
            }

            var chosenIndex = candidates[SecureInt(candidates.Count)];
            usedIndices.Add(chosenIndex);
            selected.Add(WordList[chosenIndex]);
        }

        return selected;
    }

    /// <summary>
    /// The pool size the word choices were actually drawn from, as a geometric mean.
    /// </summary>
    /// <remarks>
    /// NOT the full list. Fitting words to a length budget narrows the choice for each position,
    /// so scoring against all 7,771 would credit entropy the generator did not have. Counting the
    /// words that share each selected word's length is the honest denominator.
    /// </remarks>
    private static int EffectivePool(List<string> selectedWords)
    {
        if (selectedWords.Count == 0) return 1;

        var logSum = 0.0;
        foreach (var word in selectedWords)
        {
            var candidates = LengthCounts.TryGetValue(word.Length, out var count) ? count : 1;
            logSum += Log2(Math.Max(1, candidates));
        }

        return Math.Max(1, (int)Math.Floor(Math.Pow(2.0, logSum / selectedWords.Count)));
    }

    /// <summary>
    /// Assign capitalisation styles, balanced for the word count and with no two neighbours alike.
    /// </summary>
    /// <remarks>
    /// The balance is what makes the style choice worth entropy: an unbalanced draw could emit
    /// six lowercase words, which is one arrangement rather than thirty. The adjacency re-shuffle
    /// is capped at 20 tries and then accepted as-is - it is a readability preference, and looping
    /// until it succeeds would be an unbounded wait on a random event.
    /// </remarks>
    private static List<string> ApplyStyles(List<string> words)
    {
        var styles = new List<int> { StyleUpper, StyleLower, StyleTitle };
        Shuffle(styles);

        var assigned = new List<int>(words.Count);
        switch (words.Count)
        {
            case 2:
                assigned.Add(styles[0]);
                assigned.Add(styles[1]);
                break;
            case 3:
                assigned.AddRange(styles);
                break;
            case 4:
                assigned.AddRange(styles);
                assigned.Add(styles[SecureInt(styles.Count)]);
                ShuffleAvoidingAdjacentDuplicates(assigned);
                break;
            case 5:
                assigned.Add(styles[0]);
                assigned.Add(styles[0]);
                assigned.Add(styles[1]);
                assigned.Add(styles[1]);
                assigned.Add(styles[2]);
                ShuffleAvoidingAdjacentDuplicates(assigned);
                break;
            case 6:
                assigned.Add(styles[0]);
                assigned.Add(styles[0]);
                assigned.Add(styles[1]);
                assigned.Add(styles[1]);
                assigned.Add(styles[2]);
                assigned.Add(styles[2]);
                ShuffleAvoidingAdjacentDuplicates(assigned);
                break;
            default:
                for (var i = 0; i < words.Count; i++) assigned.Add(styles[i % styles.Count]);
                break;
        }

        var converted = new List<string>(words.Count);
        for (var i = 0; i < words.Count; i++) converted.Add(ApplyStyle(words[i], assigned[i]));
        return converted;
    }

    /// <summary>
    /// Arrange the styles so no two neighbours match, and GUARANTEE it rather than usually manage it.
    /// </summary>
    /// <remarks>
    /// The shuffle-and-retry loop is kept because it is what makes the arrangement random in the
    /// ordinary case, and the entropy score counts style arrangements. But it used to give up
    /// after 20 tries and return whatever it had, so a password could ship with two adjacent
    /// ALLCAPS words - while AC14 states the property as a guarantee and the plan's method
    /// describes it as one (review finding cpr-13).
    ///
    /// The repair pass closes that. It is deterministic and only runs when 20 random shuffles all
    /// failed, which is rare: for the multisets this generator builds - at most a pair of each of
    /// three styles - a valid arrangement always exists, so the repair always succeeds.
    /// </remarks>
    private static void ShuffleAvoidingAdjacentDuplicates(List<int> styles)
    {
        Shuffle(styles);
        for (var attempt = 0; attempt < 20 && HasAdjacentDuplicate(styles); attempt++) Shuffle(styles);

        RepairAdjacentDuplicates(styles);
    }

    /// <summary>
    /// Swap any neighbour collision away, deterministically.
    /// </summary>
    /// <remarks>
    /// For each position that matches its predecessor, find a later element that differs from both
    /// that predecessor and from whatever currently follows the collision, and swap it in. No
    /// multiset this generator produces can defeat it: with counts of at most two across three
    /// styles, no style ever exceeds the ceiling that makes a valid arrangement impossible.
    /// </remarks>
    private static void RepairAdjacentDuplicates(List<int> styles)
    {
        if (styles.Count < 2 || !HasAdjacentDuplicate(styles)) return;

        // Rebuild rather than swap. A local swap pass looks adequate and is not: on [0,0,1,1,2,2]
        // it fixes the head and strands a collision in the tail, because by the time it reaches
        // the last pair there is nothing left to swap with. The direct unit test caught that; the
        // end-to-end draw test could not, since 20 random shuffles almost always succeed and the
        // repair rarely runs at all.
        //
        // Greedy by remaining count is the standard arrangement for this problem and is correct
        // whenever no style occupies more than half the positions (rounded up). The multisets this
        // generator builds never come close - at most a pair of each of three styles - so it
        // always succeeds.
        var remaining = new Dictionary<int, int>();
        foreach (var style in styles)
        {
            remaining[style] = remaining.TryGetValue(style, out var c) ? c + 1 : 1;
        }

        var rebuilt = new List<int>(styles.Count);
        var previous = -1;

        while (rebuilt.Count < styles.Count)
        {
            var next = -1;
            var best = 0;

            foreach (var (style, count) in remaining)
            {
                if (count <= 0 || style == previous) continue;
                if (count > best)
                {
                    best = count;
                    next = style;
                }
            }

            if (next < 0)
            {
                // Unreachable for this generator's multisets. Leaving the original arrangement is
                // the safe answer: the styles are still balanced and the only cost is a repeated
                // neighbour, where inventing a style would unbalance the distribution the entropy
                // score is calculated from.
                return;
            }

            rebuilt.Add(next);
            remaining[next]--;
            previous = next;
        }

        styles.Clear();
        styles.AddRange(rebuilt);
    }

    private static bool HasAdjacentDuplicate(List<int> styles)
    {
        for (var i = 0; i < styles.Count - 1; i++)
        {
            if (styles[i] == styles[i + 1]) return true;
        }
        return false;
    }

    private static string ApplyStyle(string word, int style) => style switch
    {
        StyleUpper => word.ToUpperInvariant(),
        StyleLower => word.ToLowerInvariant(),
        _ => word.Length == 0 ? string.Empty : char.ToUpperInvariant(word[0]) + word[1..],
    };

    private static int BaseLength(List<string> words) =>
        words.Sum(w => w.Length) + Math.Max(0, words.Count - 1);

    /// <summary>
    /// Build the padding characters: always at least one digit, a symbol too once there is room,
    /// then roughly 85% digits and 15% symbols, shuffled.
    /// </summary>
    private static List<char> BuildPadding(int count)
    {
        var chars = new List<char>(count);
        if (count == 0) return chars;

        chars.Add(RandomDigit());
        if (count >= 2) chars.Add(Separators[SecureInt(Separators.Length)]);

        while (chars.Count < count)
        {
            chars.Add(SecureInt(100) < 15 ? Separators[SecureInt(Separators.Length)] : RandomDigit());
        }

        Shuffle(chars);
        return chars;
    }

    /// <summary>
    /// Spread the padding across every slot - before the first word, each gap, and after the last.
    /// </summary>
    /// <remarks>
    /// Seeding distinct slots first, in shuffled order, is what stops every password ending in a
    /// numeric tail. Scattering only the remainder keeps the distribution from collapsing back
    /// onto one slot.
    /// </remarks>
    private static string[] SplitPaddingIntoSlots(List<char> padding, int slotCount)
    {
        var slots = new string[slotCount];
        for (var i = 0; i < slotCount; i++) slots[i] = string.Empty;
        if (slotCount == 0) return slots;

        var remaining = new List<char>(padding);
        var order = Enumerable.Range(0, slotCount).ToList();
        Shuffle(order);

        var guaranteed = Math.Min(slotCount, remaining.Count);
        for (var i = 0; i < guaranteed; i++)
        {
            var last = remaining.Count - 1;
            slots[order[i]] += remaining[last];
            remaining.RemoveAt(last);
        }

        foreach (var ch in remaining) slots[SecureInt(slotCount)] += ch;

        return slots;
    }

    private static string Assemble(List<string> words, int targetLength)
    {
        var paddingCount = Math.Max(0, targetLength - BaseLength(words));
        var slots = SplitPaddingIntoSlots(BuildPadding(paddingCount), words.Count + 1);

        var builder = new StringBuilder();
        builder.Append(slots[0]);

        for (var i = 0; i < words.Count; i++)
        {
            builder.Append(words[i]);
            if (i < words.Count - 1)
            {
                builder.Append(Separators[SecureInt(Separators.Length)]);
                builder.Append(slots[i + 1]);
            }
        }

        builder.Append(slots[words.Count]);
        return builder.ToString();
    }

    private static double PaddingEntropy(int separatorCount, int paddingCount) => paddingCount switch
    {
        0 => 0.0,
        1 => Log2(10.0),
        _ => Log2(10.0) + Log2(separatorCount) + Log2(10.0) * (paddingCount - 2),
    };

    private static double EntropyBits(int wordCount, int separatorCount, int paddingCount, int effectivePool)
    {
        var wordEntropy = Log2(effectivePool) * wordCount;
        var separatorEntropy = separatorCount > 1 ? Log2(separatorCount) * (wordCount - 1) : 0.0;
        var paddingEntropy = PaddingEntropy(separatorCount, paddingCount);

        var styleArrangements = wordCount switch
        {
            2 or 3 => 6.0,
            4 => 18.0,
            5 => 36.0,
            6 => 30.0,
            _ => 6.0,
        };

        return wordEntropy + separatorEntropy + paddingEntropy + Log2(styleArrangements);
    }

    private static double Log2(double value) => Math.Log2(value);

    private static char RandomDigit() => (char)('0' + SecureInt(10));

    /// <summary>Uniform in [0, maxExclusive). RandomNumberGenerator.GetInt32 rejection-samples.</summary>
    private static int SecureInt(int maxExclusive) => RandomNumberGenerator.GetInt32(maxExclusive);

    /// <summary>Uniform in [min, max], both ends included.</summary>
    private static int SecureRange(int min, int max) => min + SecureInt(max - min + 1);

    /// <summary>Fisher-Yates, drawing from the CSPRNG.</summary>
    private static void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = SecureInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Read the word list from the assembly, never from disk.
    /// </summary>
    /// <remarks>
    /// Embedded rather than a file beside the binary: a file on the host is editable by anyone
    /// who can write to the deploy folder, and swapping in a ten-word list would silently cut
    /// every password's entropy while every test still passed. Compiled in, the list can only
    /// change through a diff.
    /// </remarks>
    private static string[] LoadWordList()
    {
        var assembly = typeof(PasswordGenerator).Assembly;
        const string resourceName = "ExchangeAdminWeb.Resources.CloudPasswordWordList.txt";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded word list '{resourceName}' is missing from the assembly. The password generator cannot run without it.");

        using var reader = new StreamReader(stream);
        var words = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) words.Add(trimmed);
        }

        ValidateWordList(words);
        return words.ToArray();
    }

    /// <summary>The word count the provenance record measures. Pinned, not a floor.</summary>
    /// <remarks>
    /// Exact rather than a minimum so that changing the pool is visible as a diff to a stated
    /// number, which is what `Resources/CloudPasswordWordList.provenance.md` exists to make
    /// possible. A replacement list is then a deliberate two-file edit rather than a silent
    /// entropy change.
    /// </remarks>
    private const int ExpectedWordCount = 7771;

    /// <summary>
    /// Refuse a word list that would make the entropy score dishonest.
    /// </summary>
    /// <remarks>
    /// THE ORIGINAL CHECK WAS A COUNT FLOOR AND THAT WAS NOT ENOUGH. A list of 7,771 copies of
    /// "aaa" passed it: every selected word then scored against a 7,771-entry length bucket, so
    /// the computed entropy cleared 60 bits while the real entropy was close to zero, and the
    /// generator reported a strong password it had not produced (review finding cpr-12).
    ///
    /// The entropy calculation assumes three properties of the pool - that the words are DISTINCT,
    /// that they fall in the documented 3-9 character range, and that the pool is the measured
    /// one. None was checked. All three are now, at load, before a single password is generated,
    /// and each failure refuses rather than degrades.
    /// </remarks>
    private static void ValidateWordList(List<string> words)
    {
        if (words.Count != ExpectedWordCount)
        {
            throw new InvalidOperationException(
                $"The embedded word list holds {words.Count} words; this generator's entropy figures are measured against {ExpectedWordCount}. "
                + "If the pool was replaced deliberately, re-measure it and update both ExpectedWordCount and Resources/CloudPasswordWordList.provenance.md.");
        }

        var distinct = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
        if (distinct.Count != words.Count)
        {
            // Duplicates are the dangerous malformation: they inflate every length bucket, so the
            // score credits choices the generator never had.
            throw new InvalidOperationException(
                $"The embedded word list holds {words.Count - distinct.Count} duplicate word(s). Duplicates inflate the effective-pool calculation, "
                + "so the measured entropy would overstate the real entropy. Refusing to run.");
        }

        foreach (var word in words)
        {
            if (word.Length is < 3 or > 9)
            {
                throw new InvalidOperationException(
                    $"The embedded word list contains a {word.Length}-character entry. The documented pool is 3-9 characters and the "
                    + "length-fitting logic assumes it. Refusing to run.");
            }

            foreach (var c in word)
            {
                if (c is (< 'a' or > 'z') and not '-')
                {
                    throw new InvalidOperationException(
                        "The embedded word list contains an entry outside the documented lowercase-ASCII-plus-hyphen shape. "
                        + "Refusing to run rather than emit passwords with unverified characters.");
                }
            }
        }
    }

    private static Dictionary<int, int> BuildLengthCounts(string[] words)
    {
        var counts = new Dictionary<int, int>();
        foreach (var word in words)
        {
            counts[word.Length] = counts.TryGetValue(word.Length, out var existing) ? existing + 1 : 1;
        }
        return counts;
    }
}
