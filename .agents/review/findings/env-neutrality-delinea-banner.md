# env-neutrality-delinea-banner: Module Config's Delinea banner names a deployment-specific secret template

**Severity**: LOW - environment neutrality (repo-guidance Architectural Invariant 7): the Module Configuration page's
Delinea banner names one deployment's secret template by name as behaviour text. Noticed by the help-sweep
implementer (2026-11-09), not changed in that slice (out of its scope).
**Status**: Admitted 2026-11-09; awaiting a slice
**Reviewer**: implementer (help-sweep hand-back)

## Fix shape

Describe the template generically ("the Azure app registration secret template your Secret Server uses") or take
the name from the configured secret at run time; no deployment name in source. Test: a stand-in asserting the banner
text carries no template name.
