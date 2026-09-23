# Cloud Password Reset word list - provenance

`CloudPasswordWordList.txt` is the word pool for `Services/PasswordGenerator.cs`. This file
records where it came from and what it measures, so that a later edit shrinking or altering the
pool shows up as a diff against a stated number rather than as a silent cut to the entropy of
every password the module issues.

## Source and licence

Copied verbatim from `wordlist.txt` in the owner's own password generator,
`github.com/AlsoBeltrix/PasswordGenerator` (mirrored at `ashbexutil1/gitea/mcoelho/PasswordGenerator`).
That repository declares `license = "MIT"` in its `Cargo.toml` and is authored by the owner of
this repository, so redistribution here is permitted. Its `Cargo.toml` describes the list as the
EFF word list; the EFF long list is published under CC BY 3.0 US, which also permits
redistribution. Checked 2026-09-23, as the plan's S2 requires before the copy lands.

This is a copy, not a dependency. Nothing in this app reads that repository at build or run time.

## Measured contents

Counted 2026-09-23 against the copy in this repository.

- **7,771 words**, one per line, no blank lines.
- **All lowercase ASCII.** No uppercase, no accented characters, no digits.
- **Exactly four contain a hyphen:** `drop-down`, `felt-tip`, `t-shirt`, `yo-yo`. They are the
  only reason a generated password can contain `-` outside the separator set, which is why the
  hyphen has to be confirmed against Entra's allowed characters alongside `!@#$%&*?+=`.
- **Length histogram**, which the generator's effective-pool calculation depends on:

  | Length | Words |
  |---|---|
  | 3 | 82 |
  | 4 | 467 |
  | 5 | 928 |
  | 6 | 1,370 |
  | 7 | 1,590 |
  | 8 | 1,777 |
  | 9 | 1,557 |
  | **Total** | **7,771** |

## Why the histogram is recorded and not just the count

`PasswordGenerator.EffectivePool` scores word entropy against the number of list words sharing
each selected word's LENGTH, not against the full 7,771, because fitting words to a character
budget narrows the real choice. The shape of this table is therefore an input to the entropy
floor, not a curiosity: removing most of the 3- and 4-character words, say, would change the
scoring for short passwords without changing the total by much.

A test pins the total. If the pool is ever replaced, re-measure this table in the same commit.
