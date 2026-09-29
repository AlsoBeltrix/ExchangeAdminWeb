"""Version bump + registry re-anchor for the read-only grid fix. Deleted after it runs."""
import difflib
import io
import re
import subprocess
import sys

# --- version bump ---------------------------------------------------------
MC = "Modules/ModuleCatalog.cs"
s = io.open(MC, encoding="utf-8", newline="").read()
old = '            Version = "1.20.5",'
new = '''            // 1.20.6: read-only operators saw every batch row one column left of its
            // heading. The header drew an unconditional spacer for the tick-box column while
            // the row only drew a tick box when canManage, so without MigrationManage the name
            // sat under the blank and Failed under a heading that was not rendered.
            // Pre-existing; invisible to everyone who built it, because they all had the
            // permission.
            Version = "1.20.6",'''
if s.count(old) != 1:
    sys.exit("version anchor")
io.open(MC, "w", encoding="utf-8", newline="").write(s.replace(old, new))

# --- registry re-anchor ---------------------------------------------------
RAZOR = "Components/Pages/Migration.razor"
REG = "ExchangeAdminWeb.Tests/ClickGateRegistry.cs"

old_lines = subprocess.check_output(
    ["git", "show", "HEAD:" + RAZOR], text=True, encoding="utf-8").split("\n")
new_lines = io.open(RAZOR, encoding="utf-8", newline="").read().split("\n")

mapping = {}
for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(
        None, old_lines, new_lines, autojunk=False).get_opcodes():
    if tag == "equal":
        for k in range(i2 - i1):
            mapping[i1 + k + 1] = j1 + k + 1

reg = io.open(REG, encoding="utf-8", newline="").read()
a = reg.index("ExpectedLineCount = ")
b = reg.index("ExpectedLineCount = 411,")
blk = reg[a:b]

ENTRY = re.compile(
    r'new (ExemptControl|NonButtonTarget|DomSyncedControl|UngatedDomSyncedControl|'
    r'KeyboardPath|HarmlessKeyboardPath)\((\d+),')

unmapped = []
moved = []


def fix(m):
    kind, line = m.group(1), int(m.group(2))
    if line not in mapping:
        unmapped.append((kind, line))
        return m.group(0)
    n = mapping[line]
    if n != line:
        moved.append((kind, line, n))
    return "new {0}({1},".format(kind, n)


blk = ENTRY.sub(fix, blk)
blk = re.sub(r"GatedTwinButtonLine: (\d+)",
             lambda m: "GatedTwinButtonLine: {0}".format(
                 mapping.get(int(m.group(1)), int(m.group(1)))), blk)

count = len(new_lines) - (1 if new_lines and new_lines[-1] == "" else 0)
blk = re.sub(r"ExpectedLineCount = \d+,",
             "ExpectedLineCount = {0},".format(count), blk, count=1)

io.open(REG, "w", encoding="utf-8", newline="").write(reg[:a] + blk + reg[b:])

print("bumped; line count {0}".format(count))
for kind, x, y in moved:
    print("  {0} {1} -> {2}".format(kind, x, y))
if unmapped:
    print("UNMAPPED:")
    for kind, line in unmapped:
        print("  {0} {1}".format(kind, line))
    sys.exit(1)
