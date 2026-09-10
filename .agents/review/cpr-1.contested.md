# cpr-1: DECLINED - "Self-Owned Cloud Account Gap" (reviewer: material change)

Declined at intake, 2026-09-10, on the owner's challenge ("what's the issue with a user
resetting a password to an account they own?").

The finding is that an L2 operator could reset a cloud-only account whose derived on-prem
owner is the operator, and so receive the password under the main permission alone. That is
true and it is not a hole. If the derivation resolves to the operator, the operator already
owns and uses that cloud account; mailing them a new password for it hands them no privilege
they did not already hold. There is no escalation, so there is nothing for a guard to stop.

The cited precedent does not transfer. `ValidateSelfGrantAsync`
(`Services/PermissionValidator.cs:280`) blocks an operator granting themselves rights over
*someone else's* mailbox or calendar - acquiring access they had none of. Resetting your own
account's password is the opposite shape.

The residual risk the finding gestures at is a derivation that resolves to the wrong person.
That is real, it is already named in the plan (corroboration, and refuse-on-ambiguity), and
it misfires identically regardless of who clicks the button. A self-check would not detect
it: the operator is not the owner in that case, which is exactly the failure.

The plan's self-reset section now records this disposition so the same finding is not
re-raised from the Graph surface alone.

Owner may overrule; the reviewer's text is in
`C:/Users/mcoelho/AppData/Local/Temp/openreview-cpr-last.txt` and quoted in the plan's
Review log.

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / fallback (openreview),
over `c493b2a..7c47c3c`, verdict "Acceptable with changes" (3 material changes),
capability_ok true.
