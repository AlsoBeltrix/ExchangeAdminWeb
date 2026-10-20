import io

# 1. The installer must not name the deploy script (Architectural Invariant 1, enforced by
#    DeployInvariants.Tests.ps1). Second time I have tripped this guard with a comment.
p = 'tools/Install-ExchangeAdminWeb.ps1'
s = io.open(p, encoding='utf-8', newline='').read()
old = '''        # and never creates one, which is why deploy.ps1 fails on exactly this condition.
        # Proceeding would mirror new binaries over an instance that cannot start, with no
        # snapshot, and report success.'''
new = '''        # and never creates one, which is why the ADI deployment tooling fails on exactly
        # this condition too. Proceeding would mirror new binaries over an instance that
        # cannot start, with no snapshot, and report success.'''
assert s.count(old) == 1, 'installer comment anchor'
io.open(p, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
assert 'deploy.ps1' not in io.open(p, encoding='utf-8').read(), 'still names the deploy script'
print('installer comment reworded')

# 2. My guard matched the word inside an explanatory comment. Strip comments first - the
#    claim is about the CODE, and a guard that reads comments proves nothing about it.
p = 'tests/ps/MoveConfigDbToShared.Tests.ps1'
s = io.open(p, encoding='utf-8', newline='').read()
old = '''            $source = Get-Content $script:ScriptPath -Raw
            $body = $source.Substring($source.IndexOf('function Invoke-AppWarmup'))
            $body = $body.Substring(0, $body.IndexOf('function Assert-SchemaReadyLogged'))

            $body | Should -Not -Match 'Invoke-WebRequest'
            $body | Should -Match '\\[System\\.Net\\.WebRequest\\]::Create'
            $body | Should -Match 'AllowAutoRedirect = \\$false' '''
new = '''            $source = Get-Content $script:ScriptPath -Raw
            $body = $source.Substring($source.IndexOf('function Invoke-AppWarmup'))
            $body = $body.Substring(0, $body.IndexOf('function Assert-SchemaReadyLogged'))

            # Comments stripped: the claim is about the CODE, and the remarks above
            # deliberately NAME Invoke-WebRequest to explain why it is not used. A guard
            # that reads prose proves nothing about what executes.
            $code = ($body -split "`n" | Where-Object { $_.TrimStart() -notlike '#*' }) -join "`n"

            $code | Should -Not -Match 'Invoke-WebRequest'
            $code | Should -Match '\\[System\\.Net\\.WebRequest\\]::Create'
            $code | Should -Match 'AllowAutoRedirect = \\$false' '''
assert s.count(old) == 1, 'guard anchor'
io.open(p, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
print('guard now reads code, not comments')
