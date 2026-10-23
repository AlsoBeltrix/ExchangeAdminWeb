# aud-a29g: prerendered pages still render and run OnInitialized under a stale ticket

**Severity**: MEDIUM, design - the aud-a29b/c/d/e revalidation closes the circuit when an operator's authorization changes, and no handler can run under a stale ticket. But the prerender pass of a page request still renders the page and runs `OnInitialized` / `OnInitializedAsync` against the request's cached principal before the circuit's revalidation runs, so a revoked operator who requests a page still sees it rendered (reads in `OnInitialized` included) once. Closing it costs a directory read per page request, which is why the aud-a29b record left it for an owner cost decision; the 2026-10-23 evening ruling resolves everything open, so it is dispatched.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded by the supervisor from the aud-a29b record's hand-back)

## Evidence

At `4498da49`: `.agents/review/findings/aud-a29b.md` (the mechanism and the options the owner was given); `Program.cs` component registration and the revalidating provider added by aud-a29b (`cb3be91`, `702f66f`, `0d30442`); `Components/App.razor` render mode. The implementing agent locates the exact prerender path and quotes it before writing the approach.

Trigger: an operator is removed from an allowed group, then requests a page URL. Predicted failure: the page renders its initial state (and any `OnInitialized` read) once before the circuit revalidation drops it.

## Approach (to be written by the implementing agent before code)

- Candidates: (a) revalidate once per page request in middleware or in the host page before prerender (the per-request directory read, cached for the aud-a29b interval so repeated requests inside the interval pay nothing); (b) disable prerender for authenticated pages so nothing renders before the circuit's first revalidation. Choose with the measured cost written down; (a) must reuse the aud-a29b revalidation code and cache rather than a second implementation.
- Guard: a test that drives a page request under a principal the revalidator refuses and asserts no page body and no `OnInitialized` side effect.
- Mutation: the per-request check removed.

## Guard proof

Pending.