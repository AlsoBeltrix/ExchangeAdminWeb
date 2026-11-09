# rev-f2: an unreadable virtual-directory list of an application elsewhere is downgraded to a warning

**Severity**: MEDIUM - fail-open serving-pool check. When the probe cannot read an application's virtual directories,
the issue node keeps only that application's own physical path, `$couldReach` is decided from that path, and the node
is downgraded to a warning. One of its unread virtual directories could point inside the deploy folder, so a deploy
or promotion could mirror over files another pool still serves.
**Status**: Admitted 2026-11-09 from codex review F (`.agents/review/rev-f.result.txt`); awaiting a slice
**Reviewer**: codex (Portkey) / configured route / xhigh
**Range reviewed**: 341750c0..59522ca6 (merge sweep-g)

## Evidence (master 59522ca6)

- `tools/AppPoolControl.psm1:630-642`: on a virtual-directory read failure the issue node is built with
  `PhysicalPath = $owner.PhysicalPath`.
- `tools/AppPoolControl.psm1:692-711`: `$couldReach` from that path; the warning text admits "Their unread
  applications or virtual directories were not seen; one pointing into '$PhysicalPath' would not be either."

## Failure scenario

`Other/Tools` at `D:\other\Tools` has a virtual directory at `D:\sites\App\assets` under another pool. Reading its
virtual directories fails. `$couldReach` sees only `D:\other\Tools`, warns, and the mirror proceeds.

## Fix shape (reviewer's proposal; verify first)

An unreadable virtual-directory list refuses (the item-34 rule "refuses only when it could reach the folder" cannot be
applied to a list that was not read), unless the probe can prove the application has no virtual directories. Test: an
elsewhere application whose virtual-directory read fails refuses; the existing test asserts only the warning.
