namespace ExchangeAdminWeb.Services;

/// <summary>
/// Tracks how many unsaved edits each section of an admin page holds, so one save bar can report
/// accurately -- how many changes, and in which sections -- and the navigation guard knows when to
/// warn.
/// </summary>
/// <remarks>
/// A service rather than page fields because this repo has no bUnit harness -- the same reason
/// <c>MessageTraceExportListing</c> and <c>ProtectedPrincipalEntryValidator</c> exist. Dirty
/// tracking that silently fails is precisely the defect that loses an operator's edit, so it is
/// the part that must be testable.
///
/// Replaces the previous model: eight separate Save buttons across two pages, each saving one
/// section, with no indication anywhere that another section was still unsaved
/// (docs/AdminUIRedesign-Plan.md).
///
/// Counts EDITS, not sections. The earlier version held a set of dirty section names, so ten
/// added protected groups and one toggled module both read "1 unsaved change" -- the number was
/// the count of sections, which an operator reads as the count of changes (owner ruling
/// 2026-09-02). Sections are still named, because "1 unsaved change" on a five-tab page says
/// something is wrong but not where.
///
/// Two ways to record an edit, because the pages hold two shapes of pending state:
/// <see cref="Increment"/> for a discrete operator action with nothing to compare against, and
/// <see cref="SetCount"/> for a section that can diff its working copy against what it loaded --
/// which is what makes an edit undone before saving net back to clean.
/// </remarks>
public sealed class AdminPageDirtyState
{
    // Only sections with at least one pending edit have an entry, so the key set is exactly the
    // dirty sections and no cleanup pass is needed to keep the summary honest.
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the pending-edit counts change, so the save bar and tabs can re-render.</summary>
    public event Action? Changed;

    /// <summary>True when any section has unsaved edits.</summary>
    public bool IsDirty => DirtyCount > 0;

    /// <summary>How many individual pending edits are held, across every section.</summary>
    public int DirtyCount => _counts.Values.Sum();

    /// <summary>How many sections hold pending edits (as opposed to how many edits).</summary>
    public int DirtySectionCount => _counts.Count;

    /// <summary>The unsaved sections, in a stable order so the summary text does not flicker.</summary>
    public IReadOnlyList<string> DirtySections =>
        _counts.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when this specific section is unsaved (drives the dot on its tab).</summary>
    public bool IsSectionDirty(string section) => SectionCount(section) > 0;

    /// <summary>Pending edits in one section; zero when it is clean.</summary>
    public int SectionCount(string section) =>
        !string.IsNullOrWhiteSpace(section) && _counts.TryGetValue(section, out var count) ? count : 0;

    /// <summary>
    /// Records one more pending edit in a section, for an action with nothing to diff against.
    /// </summary>
    public void Increment(string section) => SetCount(section, SectionCount(section) + 1);

    /// <summary>
    /// Sets a section's pending-edit count outright, for a page that can count its own pending
    /// edits by diffing its working copy against what it loaded.
    /// </summary>
    /// <remarks>
    /// Zero or less clears the section: that is the whole point of this overload rather than a
    /// running tally -- a module toggled on and back off, or a group added and then removed, is no
    /// longer a pending change and must stop being counted. Blank section names are ignored rather
    /// than tracked under an empty key, which would make the count right and the summary useless.
    /// </remarks>
    public void SetCount(string section, int count)
    {
        if (string.IsNullOrWhiteSpace(section))
            return;

        bool changed;
        if (count <= 0)
        {
            changed = _counts.Remove(section);
        }
        else if (SectionCount(section) == count)
        {
            changed = false;
        }
        else
        {
            _counts[section] = count;
            changed = true;
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Marks one section clean, after that section saved successfully.</summary>
    public void ClearSection(string section) => SetCount(section, 0);

    /// <summary>Marks everything clean, after a successful save-all or a discard.</summary>
    public void Clear()
    {
        if (_counts.Count == 0)
            return;

        _counts.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The save bar's text: how many edits are pending, and which sections hold them.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. The number has to be the edit count, because an operator who
    /// made ten changes and is told there is one will believe nine were lost or never registered.
    /// Naming the sections has to stay, because a count alone on a five-tab page tells an operator
    /// something is wrong but not where -- the failure the eight scattered Save buttons already had.
    /// </remarks>
    public string Summary()
    {
        var sections = DirtySections;
        if (sections.Count == 0)
            return string.Empty;

        var total = DirtyCount;
        var noun = total == 1 ? "unsaved change" : "unsaved changes";

        return $"{total} {noun} in {string.Join(", ", sections)}";
    }
}
