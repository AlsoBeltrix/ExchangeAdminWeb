namespace ExchangeAdminWeb.Services;

/// <summary>
/// Tracks which sections of an admin page have unsaved edits, so one save bar can report
/// accurately and the navigation guard knows when to warn.
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
/// </remarks>
public sealed class AdminPageDirtyState
{
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the dirty set changes, so the save bar and tabs can re-render.</summary>
    public event Action? Changed;

    /// <summary>True when any section has unsaved edits.</summary>
    public bool IsDirty => _dirty.Count > 0;

    /// <summary>How many sections are unsaved.</summary>
    public int DirtyCount => _dirty.Count;

    /// <summary>The unsaved sections, in a stable order so the summary text does not flicker.</summary>
    public IReadOnlyList<string> DirtySections =>
        _dirty.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when this specific section is unsaved (drives the dot on its tab).</summary>
    public bool IsSectionDirty(string section) => _dirty.Contains(section);

    /// <summary>
    /// Marks a section dirty or clean. Blank names are ignored rather than tracked under an empty
    /// key, which would make the count right and the summary useless.
    /// </summary>
    public void Set(string section, bool dirty)
    {
        if (string.IsNullOrWhiteSpace(section))
            return;

        var changed = dirty ? _dirty.Add(section) : _dirty.Remove(section);

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Marks one section clean, after that section saved successfully.</summary>
    public void ClearSection(string section) => Set(section, false);

    /// <summary>Marks everything clean, after a successful save-all or a discard.</summary>
    public void Clear()
    {
        if (_dirty.Count == 0)
            return;

        _dirty.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The save bar's text: names the offending section when there is one, counts them when
    /// there are several.
    /// </summary>
    /// <remarks>
    /// Naming the section is the point. "1 unsaved change" on a four-tab page tells an operator
    /// something is wrong but not where, which is the failure the eight scattered buttons already
    /// had.
    /// </remarks>
    public string Summary()
    {
        var sections = DirtySections;

        return sections.Count switch
        {
            0 => string.Empty,
            1 => $"1 unsaved change in {sections[0]}",
            _ => $"{sections.Count} unsaved changes in {string.Join(", ", sections)}"
        };
    }
}
