# rev-f2: an unreadable virtual-directory list of an application elsewhere is downgraded to a warning

**Severity**: MEDIUM - fail-open serving-pool check. When the probe cannot read an application's virtual directories,
the issue node keeps only that application's own physical path, `$couldReach` is decided from that path, and the node
is downgraded to a warning. One of its unread virtual directories could point inside the deploy folder, so a deploy
or promotion could mirror over files another pool still serves.
**Status**: Merged dde1b7fb; closed by codex review G 2026-11-09
**Branch**: `fix/rev-f` (worktree `D:\source\ExchangeAdminWeb-wt\rev-f`, from master `454fa2b7`)
**Commit**: `0bc510c6` (`fix(rev-f2): an unread application or virtual-directory list refuses the serving-pool check`) |
**Implementer**: Claude (supervised agent)
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

## Verified real (master 454fa2b7)

`tools/AppPoolControl.psm1:640-642`: the virtual-directory catch emitted
`[pscustomobject]@{ ...; PhysicalPath = $owner.PhysicalPath; Issue = "its virtual directories cannot be read: ..." }`, and
`$couldReach` (`:692-701`) judged it by that one path; `:708-711` then warned "Their unread applications or virtual
directories were not seen; one pointing into '$PhysicalPath' would not be either." The same holds for the application-list
catch (`:617-619`, the site's own path only) and the XPath-literal catch (`:624-628`).

## As built

- Every issue node the default probe yields carries `PathsRead = $false`: the site that cannot be read, a site whose
  applications cannot be listed, and each application whose virtual directories cannot be read (read error, missing
  root virtual directory, or a site name holding both quote characters).
- `$couldReach` returns true first for any issue node not marked `PathsRead = $true`, so such a node refuses wherever its
  own path is. The refusal (unchanged lead "Cannot read the IIS sites and applications that could serve ...") names the
  node, the failed read and adds "(what was not read could point into '<folder>', under any pool)".
- The item-34 warning remains, reworded, for an issue node a probe reports with `PathsRead = $true` (every path it holds
  read) and elsewhere. The default probe reports none; a probe node without the property is taken as not fully read.
- Help: the function's description, README deploy section ("A site, application or virtual directory IIS cannot read
  ..."). No caller changed.

## Where the code goes beside the fix shape

- The finding and brief name the virtual-directory list. An unread APPLICATION list is the same hole one level up (an
  unseen application can sit at the folder under another pool, the cdt-61 case), and the brief's mandate is "a read that
  cannot be completed refuses with a named reason", so it refuses too. With both, the default probe yields no node that
  only warns: in practice item 34's downgrade is withdrawn for every read the probe cannot complete. What item 34 still
  gives: the probe goes on past one failure and the refusal names every unreadable node at once. **Owner question**: if
  an unreadable application list on a site elsewhere should stay a warning, that is a one-line change (`PathsRead = $true`
  on that issue node) with a known fail-open.
- The cdt-61 "still refuses with an unreadable site elsewhere" test now uses a probe-supplied fully read odd node (the
  default probe cannot produce one that only warns any more); its assertion is unchanged.

## Assumptions not verified live

- That no site on the dev/prod host has an unreadable application or virtual-directory list. If one does, every deploy,
  promotion and install stop now refuses naming it (as before item 34) until it is fixed in IIS.

## Guard proof (`tests/ps/AppPoolControl.Tests.ps1`, "sweep item 34")

| Mutation | Failing tests |
|---|---|
| `tools/AppPoolControl.psm1` at `454fa2b7` | 5 of 158: the four rev-f2 "refuses ..." rows (no root virtual directory, both quote characters, application list unread, virtual-directory read fails - THE FINDING) and "a node a probe does not report as fully read ... refuses" |
| Restored | 158 of 158 pass |

"An odd node elsewhere whose paths were all read still only warns" and "a fully read application elsewhere is neither a
warning nor a refusal" pass both ways (they pin what must not refuse).

## Deploy-time checks

- A plan of each deploy on the host: no "Cannot read the IIS sites and applications" refusal unless IIS really has an
  unreadable node; if it does, the line names it.