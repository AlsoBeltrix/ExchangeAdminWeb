# rev-x1: catalog searches outside the protection checks still send no SearchBase

**Severity**: LOW-MEDIUM - same root as rev-a3/b1/c1, outside the protection path. The rev-abc implementer found these
forest-wide catalog searches still without an explicit empty SearchBase: `ADDirectorySearchService`'s picker search,
Group Management's group search, Conference Rooms' and Migration's catalog lookups. In a multi-tree forest they return
only the catalog's own domain partition. None decides authorization; Conference Rooms and Migration fail closed on a
miss; the pickers show fewer results. Note the repo's own `ADDirectorySearchService` comment records a live run where a
base-less catalog search returned both of this forest's domains, so the effect may not show here; fixed-when-reached
because the safety argument may not rest on the forest's shape.
**Status**: Admitted 2026-11-06 (from the rev-abc hand-back); awaiting a slice
**Reviewer**: implementer (rev-abc hand-back); not from a codex-commercial review

## Fix shape

Reuse `PowerShellProtectedGroupDirectory`'s `RunAt` helper shape (empty base on catalog reads, domain base on domain
reads) at each site; stand-in tests per site as in `GlobalCatalogSearchBaseTests`.
