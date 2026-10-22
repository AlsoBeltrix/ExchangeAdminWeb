# aud-a28y: Self-grant check - an unreadable config or a failed resolver skips the object-id comparison and allows the grant; a non-boolean appsettings value throws on every check

**Severity**: MEDIUM/HIGH - fail-open authorization (Known Failure Class 3). (1) `ValidateSelfGrantAsync`
compares the actor and the grantee by object id after a string match; when the module config cannot be
read or the resolver fails, `ResolveToObjectIdAsync` swallows the error and returns null, the comparison
is skipped and the grant is ALLOWED. A self-grant through an alias the string match misses therefore passes
exactly while Exchange cannot answer. (2) An appsettings `Security:PreventSelfGrant` that is not a boolean
makes `GetPreventSelfGrant` throw from `bool.Parse` on every self-grant check instead of refusing with a
reason (pre-existing; aud-a28t fixed the stored value's parse, not the appsettings one).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28t/a26q agent, 2026-10-22, candidates 2 and 3 of its hand-back)

## Evidence

At `18c62ce9`: `Services/PermissionValidator.cs` `ValidateSelfGrantAsync`, `ResolveToObjectIdAsync` (the
swallowed error, the null return and the skipped comparison), `GetPreventSelfGrant` (the `bool.Parse` on
the appsettings value).

## Approach (to be written by the implementing agent before code)

- When the resolver fails or the config is unreadable, the self-grant check REFUSES: "the self-grant check
  could not be completed: <reason>; nothing was written" - the aud-a28t refusal shape; the string match
  alone never clears a grant when the object-id comparison was wanted and unavailable.
- `GetPreventSelfGrant` treats an unparseable appsettings value like an unparseable stored value: refuse,
  name the setting, never throw.
- Tests through the validator's fakes (a resolver that throws; a config read that fails; an appsettings
  value "yes"); mutations at the call site: the null-skips-comparison path again; `bool.Parse` again.

## Guard proof

Pending.
