namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>The membership change a self-service caller requested on a single user member.</summary>
public enum MembershipOperation
{
    /// <summary>Add the user to the group (add-if-absent).</summary>
    Add,

    /// <summary>Remove the user from the group (remove-if-present).</summary>
    Remove,
}

/// <summary>Whether the directory actually needs to be written to reach the requested end state.</summary>
public enum MembershipWriteAction
{
    /// <summary>The group must be written (member added or removed).</summary>
    Write,

    /// <summary>The group is already in the requested state; no write is needed (idempotent no-op).</summary>
    AlreadySatisfied,
}

/// <summary>
/// Pure, AD-free decision core for the member add/remove write (plan
/// docs/SelfServiceGroupManagement-Plan.md task 5, section 6.5). Kept separate from the live PowerShell
/// write so the idempotency and reconciliation rules - the parts that decide whether to write and
/// whether the write succeeded - are unit-testable without a domain controller.
///
/// Two decisions live here:
/// - <see cref="PlanWrite"/> expresses membership as idempotent DESIRED STATE (add-if-absent /
///   remove-if-present, section 6.5), so a retry of an already-applied change is a safe no-op rather
///   than an error.
/// - <see cref="IsDesiredStateReached"/> is the post-write READ-BACK reconciliation (section 6.5,
///   codex F10): after the write, the group's membership is re-read and compared to the requested end
///   state, so a write that timed out AFTER committing - or silently did nothing - is detected rather
///   than reported as blind success (Known Failure Class #2).
/// </summary>
public static class MembershipChangeReconciler
{
    /// <summary>
    /// Decides whether the directory needs a write to satisfy the requested operation, given whether
    /// the member is currently in the group. Add-if-absent: adding a member already present is a no-op;
    /// remove-if-present: removing a member already absent is a no-op. Idempotent so a retry is safe.
    /// </summary>
    public static MembershipWriteAction PlanWrite(MembershipOperation operation, bool memberCurrentlyPresent)
        => operation switch
        {
            MembershipOperation.Add => memberCurrentlyPresent ? MembershipWriteAction.AlreadySatisfied : MembershipWriteAction.Write,
            MembershipOperation.Remove => memberCurrentlyPresent ? MembershipWriteAction.Write : MembershipWriteAction.AlreadySatisfied,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown membership operation."),
        };

    /// <summary>
    /// Post-write reconciliation: true when the observed membership (read back AFTER the write) matches
    /// the requested end state - present after an Add, absent after a Remove. A false result means the
    /// write did not take effect and the change must be reported as failed, never as success.
    /// </summary>
    public static bool IsDesiredStateReached(MembershipOperation operation, bool memberPresentAfterWrite)
        => operation switch
        {
            MembershipOperation.Add => memberPresentAfterWrite,
            MembershipOperation.Remove => !memberPresentAfterWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown membership operation."),
        };
}
