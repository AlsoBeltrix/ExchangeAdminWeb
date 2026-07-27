using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the pure decision core for the self-service member add/remove write (plan
/// docs/SelfServiceGroupManagement-Plan.md task 5, section 6.5). Two behaviors are pinned: idempotent
/// desired-state planning (add-if-absent / remove-if-present, so a retry is a safe no-op) and post-write
/// read-back reconciliation (the write is only "success" when the group actually reached the requested
/// end state - a timed-out-but-uncommitted write must NOT read as success).
/// </summary>
public class MembershipChangeReconcilerTests
{
    // --- PlanWrite: idempotent desired-state (add-if-absent / remove-if-present) ---

    [Fact]
    public void Add_when_member_absent_writes()
    {
        Assert.Equal(MembershipWriteAction.Write,
            MembershipChangeReconciler.PlanWrite(MembershipOperation.Add, memberCurrentlyPresent: false));
    }

    [Fact]
    public void Add_when_member_already_present_is_a_noop()
    {
        // Idempotent: re-adding a member already in the group must not attempt a write.
        Assert.Equal(MembershipWriteAction.AlreadySatisfied,
            MembershipChangeReconciler.PlanWrite(MembershipOperation.Add, memberCurrentlyPresent: true));
    }

    [Fact]
    public void Remove_when_member_present_writes()
    {
        Assert.Equal(MembershipWriteAction.Write,
            MembershipChangeReconciler.PlanWrite(MembershipOperation.Remove, memberCurrentlyPresent: true));
    }

    [Fact]
    public void Remove_when_member_already_absent_is_a_noop()
    {
        // Idempotent: removing a member not in the group must not attempt a write.
        Assert.Equal(MembershipWriteAction.AlreadySatisfied,
            MembershipChangeReconciler.PlanWrite(MembershipOperation.Remove, memberCurrentlyPresent: false));
    }

    // --- IsDesiredStateReached: post-write read-back reconciliation (codex F10) ---

    [Fact]
    public void Add_reconciles_only_when_member_present_after_write()
    {
        Assert.True(MembershipChangeReconciler.IsDesiredStateReached(MembershipOperation.Add, memberPresentAfterWrite: true));
        // A write that "succeeded" but left the member absent is NOT success (Known Failure Class #2).
        Assert.False(MembershipChangeReconciler.IsDesiredStateReached(MembershipOperation.Add, memberPresentAfterWrite: false));
    }

    [Fact]
    public void Remove_reconciles_only_when_member_absent_after_write()
    {
        Assert.True(MembershipChangeReconciler.IsDesiredStateReached(MembershipOperation.Remove, memberPresentAfterWrite: false));
        // The member is still present after a "remove" - the write did not take effect.
        Assert.False(MembershipChangeReconciler.IsDesiredStateReached(MembershipOperation.Remove, memberPresentAfterWrite: true));
    }
}
