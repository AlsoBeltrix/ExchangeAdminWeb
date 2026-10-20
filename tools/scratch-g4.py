import io


def edit(path, pairs):
    s = io.open(path, encoding='utf-8', newline='').read()
    crlf = '\r\n' in s
    for old, new, n in pairs:
        if crlf:
            old, new = old.replace('\n', '\r\n'), new.replace('\n', '\r\n')
        assert s.count(old) == n, f'{path}: x{s.count(old)} want {n} -> {old[:55]!r}'
        s = s.replace(old, new)
    io.open(path, 'w', encoding='utf-8', newline='').write(s)
    after = io.open(path, encoding='utf-8', newline='').read()
    assert all(ord(c) < 128 for c in after), path + ': non-ASCII'
    assert ('\r\n' in after) == crlf, path + ': endings'


edit('tests/ps/DeployInvariants.Tests.ps1', [
    # The probe now also guards against git being absent entirely.
    ("""        $s.Text | Should -Match '\\$commitProbeOk = \\(\\$LASTEXITCODE -eq 0\\)'""",
     """        $s.Text | Should -Match '\\$commitProbeOk = -not \\$commitProbeFailed -and \\(\\$LASTEXITCODE -eq 0\\)'""", 1),

    # The message now covers a placeholder commit as well as a missing one.
    ("""        $s.Text | Should -Match 'manifest carries no gitCommit'""",
     """        $s.Text | Should -Match 'manifest carries no usable gitCommit'""", 1),

    # My own new guard was over-escaped.
    ("""        $s.Text | Should -Match "\\\\\\\\\\$manifest\\\\\\\\.gitCommit -eq 'unknown'" """.rstrip(),
     """        $s.Text | Should -Match "gitCommit -eq 'unknown'" """.rstrip(), 1),
])

# A redirect is no longer a live application - that is the fix, so the test inverts.
edit('tests/ps/MoveConfigDbToShared.Tests.ps1', [
    ("""        It 'counts a redirect as a live application' {
            $dir = New-SettingsDir -Json '{ }'
            try {
                Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) 302 } | Should -BeTrue
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }""",
     """        It 'does NOT count a redirect as a live application' {
            # Review of A40: a 3xx returned true on the http pass and broke out of the
            # scheme loop before https was ever tried. An IIS-level http-to-https redirect
            # is exactly that case, and it proves nothing about whether the app started.
            $dir = New-SettingsDir -Json '{ }'
            try {
                Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) $script:requested.Add($Url); 302 } |
                    Should -BeFalse
                $script:requested | Should -Contain 'https://localhost/'
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }""", 1),
])

print('stale guards updated')
