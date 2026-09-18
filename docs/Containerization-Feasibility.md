# Containerization Feasibility - Plan

Status: Draft (2026-09-18). Nothing is approved. This document contains no code and asks for
no code: its deliverable is an answer to the owner's question and a recommendation about which
route to take. **The recommendation is negative for containers and positive for a cheaper
route**, and that recommendation is itself the thing being proposed for approval.

Owner request, verbatim from the queue:

> 6. Containerize the app entirely so it can be deployed elsewhere rapidly. (docker? does that
>    work with IIS?)

The parenthetical is a direct question, so it is answered first, in plain terms, before any
plan. Every platform claim below was verified against Microsoft Learn on 2026-09-18 and the
URLs are listed under "Sources". Every claim about this app was verified by reading the file
named. Anything that could not be verified is collected under "Assumptions" and is labelled as
an assumption where it appears in the body.

---

## Short answer

**Docker works with IIS only in the sense that you can install IIS inside a Windows container -
and for this app that is beside the point, because IIS is not what makes the app hard to move.**
For an ASP.NET Core app, Microsoft's own container guidance does not use IIS at all: the
published pattern is `ENTRYPOINT ["dotnet", "app.dll"]`, which runs Kestrel directly
(`building-net-docker-images`). Dropping IIS is the easy part.

The hard parts are three, in ascending order of difficulty:

1. The container must be a **Windows** container, not Linux, and a Windows container is a
   heavyweight, version-locked artifact rather than the portable thing the word "container"
   usually promises.
2. The app authenticates its **browsers** with Windows Authentication and authenticates itself
   to **Active Directory** with domain credentials. A container is never domain-joined. Making
   that work needs a gMSA plus a credential spec plus new SPNs plus a change to the wire
   protocol the deploy scripts currently force.
3. **The blocker.** The app's bootstrap credential for Delinea - the secret that unlocks every
   other secret - is read from the Windows per-user credential locker via a WinRT API. That
   store does not exist in a fresh container and cannot be baked into an image. Until that one
   seam is replaced, a container of this app cannot authenticate to anything.

Item 3 is not a container problem that a Dockerfile solves. It is an application change,
gated on an owner decision about how a non-interactive host is supposed to bootstrap its first
secret.

**Verdict: do not containerize.** The owner asked for an outcome - "deployed elsewhere
rapidly" - and containers are the expensive way to reach it here. The cheap way is to finish
`tools/Install-ExchangeAdminWeb.ps1`, which `.agents/repo-guidance.md` Architectural Invariant 1
already designates as the environment-neutral, standalone installer. It is roughly 80 percent of
the answer today; what it is missing is a prerequisite phase. See "The cheaper route".

---

## What the app actually needs from its host

This is the evidence base for everything that follows. Each row was read, not remembered.

| What | Where | What it needs from the host |
| --- | --- | --- |
| Target framework `net10.0-windows10.0.17763.0` | `ExchangeAdminWeb.csproj:4` | Windows. The `10.0.17763.0` OS suffix is not decoration: it turns on the Windows SDK / WinRT projections, which is the only reason the next row compiles. |
| Delinea bootstrap credential | `Services/CredentialManagerService.cs:1,11-12` | `using Windows.Security.Credentials;` and `new PasswordVault()`. WinRT credential locker. Microsoft's reference states plainly: "Lockers are specific to a user." |
| Delinea client uses it | `Services/DelineaService.cs:28-46` | `CredentialManagerService.ReadCredential(_credentialTarget)`; on empty it blanks `_apiUsername`/`_apiKey` and every secret fetch then fails closed. |
| App pool loads a user profile | `deploy.ps1:287`, `tools/Install-ExchangeAdminWeb.ps1:595` | `processModel.loadUserProfile = $true`. This is the repo's own evidence that the credential store is profile-bound. |
| Browser authentication | `Program.cs:38-39` | `AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate()`. |
| Hosting model | `web.config:6-8` | `AspNetCoreModuleV2`, `hostingModel="inprocess"`. The app runs inside `w3wp.exe`, not behind a reverse proxy. |
| IIS auth configuration | `deploy.ps1:372-394`, `tools/Install-ExchangeAdminWeb.ps1:691-695` | Windows auth on, kernel mode off, `useAppPoolCredentials` true, anonymous off, **and the Negotiate provider removed**. |
| App pool identity | `deploy.ps1:289-295` | `identityType 3` (a specific domain user) with a password; the installer also supports `identityType 4` (ApplicationPoolIdentity) at line 605. |
| On-prem AD, ambient identity | `Services/ADDirectorySearchService.cs:496,551,560` | `iss.ImportPSModule("ActiveDirectory")`, then `Get-ADForest` to find a global catalog and query `host:3268`. No `-Credential`: this path runs as the app pool identity. |
| On-prem AD, explicit credential | 12 services, e.g. `Services/GroupManagementService.cs` (x15), `Services/SelfServiceGroups/SelfServiceGroupService.cs:947,1472-1478` | `Import-Module ActiveDirectory` plus `-Credential` from a `PSCredential` built out of a Delinea secret. |
| The AD module itself | host-installed | `ActiveDirectory 1.0.1.0` at `C:\Windows\system32\WindowsPowerShell\v1.0\Modules\ActiveDirectory` on the current deploy host. RSAT. Not in this repo, not restored by NuGet. |
| Exchange Online | `Services/ExoConnectionPool.cs:422-443` | `Microsoft.PowerShell.SDK` 7.6.1 (`ExchangeAdminWeb.csproj:30`) hosting an in-process runspace, then `Import-Module ExchangeOnlineManagement` and `Connect-ExchangeOnline -AppId -CertificateThumbprint -Organization`. The module is host-installed from the gallery, not bundled. |
| The EXO certificate | `Services/ExoConnectionPool.cs:507-529` | `new X509Store(StoreName.My, StoreLocation.LocalMachine)`, falling back to `CurrentUser`. A Windows certificate store, with a private key the deploy scripts explicitly ACL to the app pool identity (`deploy.ps1:323`, `tools/Install-ExchangeAdminWeb.ps1:621-639`). |
| Shared config database | `Services/Storage/ConfigStorePath.cs:38-53`, `Program.cs:50-64` | An **absolute local** path that **must already exist**. UNC is refused by name. Drive-relative is refused. |
| Per-instance databases and logs | `Program.cs:82,91,28` | `config/exchangeadmin-jobs.db`, `config/exchangeadmin-usage.db`, `logs/app-.log`, all under the content root. |
| PathBase | `Program.cs:354-358` | `Application:PathBase`, default `/ExchangeAdminWeb`; the app calls `UsePathBase` itself. |
| Deploy tooling | `deploy.ps1:45-48` | `WebAdministration` and therefore **Windows PowerShell 5.1** - the script throws with that exact instruction if the `IIS:` drive is missing. |

Four independent Windows dependencies, then. The WinRT credential locker, the RSAT
`ActiveDirectory` module, the Windows certificate store, and the IIS provider in the deploy
script. Only the last of those is about IIS.

---

## 1. Windows containers or Linux containers?

**Windows. There is no Linux option, and it is not close.**

`net10.0-windows10.0.17763.0` (`ExchangeAdminWeb.csproj:4`) cannot be published for Linux at
all while `Services/CredentialManagerService.cs` exists, because `Windows.Security.Credentials`
resolves only through the OS-versioned TFM's WinRT projection. Even after deleting that file the
RSAT `ActiveDirectory` module and `X509Store(StoreLocation.LocalMachine)` both remain Windows-only.
`Microsoft.PowerShell.SDK` itself is cross-platform and is not the constraint.

Base images are available. MCR publishes `mcr.microsoft.com/dotnet/aspnet` tags
`10.0-nanoserver-ltsc2022`, `10.0-nanoserver-ltsc2025`, `10.0-windowsservercore-ltsc2022` and
`10.0-windowsservercore-ltsc2025` (verified against the MCR tag list, 2026-09-18). Nano Server
is out: the app needs RSAT and a machine certificate store, so the realistic base is
**`windowsservercore`**, the largest of the four.

Practical consequences, each of which is a real cost rather than a footnote:

- **Size.** Server Core plus the .NET 10 ASP.NET runtime plus RSAT plus the
  `ExchangeOnlineManagement` module is a multi-gigabyte image. This is the opposite of the
  lightweight artifact the word "container" implies, and it is pulled over the network to every
  new host. (The exact number is an assumption; it has not been built.)
- **Host OS lock-in.** Windows blocks a container whose build number differs from the host's,
  except that Windows 11 and Windows Server 2022 can run process-isolated WS2022 containers
  (`version-compatibility`). The compatibility matrix is strict: a Server 2022 host runs 2022
  images with process isolation and 2019/2016 images only under Hyper-V isolation, and **cannot
  run Server 2025 images at all**. So "deploy elsewhere rapidly" acquires a precondition - the
  destination host's Windows build must match the image, or Hyper-V isolation must be available
  there. Do not assume the current host's build; `.agents/machines.md` records what the current
  deploy host is, and any target host must be checked the same way at deploy time.
- **Licensing.** Windows Server container images require a licensed Windows Server host. There
  is no equivalent of "any Linux box with Docker". Confirming the licensing position for a given
  destination is the owner's, not this repo's (open question 3).
- **The current host.** Whether the existing deploy host has the container feature installed and
  a container runtime present was **not checked** - the task forbids changing the host and
  running Docker. Treat "the container platform exists here" as unverified.

---

## 2. Does IIS matter?

**IIS is load-bearing for exactly one thing today, and that one thing is replaceable. Everything
else IIS does here, the app already does for itself.**

What IIS actually contributes, by evidence:

- **Windows Authentication.** This is the load-bearing part. `web.config:6-8` uses
  `hostingModel="inprocess"`, so IIS terminates the request and performs the Windows
  authentication handshake itself. The `AddNegotiate()` call in `Program.cs:39` does not do the
  handshake in that configuration: the Negotiate package ships
  `PostConfigureNegotiateOptions`, documented as "Reconfigures the NegotiateOptions to defer to
  the integrated server authentication if present". Behind IIS in-process, the integrated server
  auth is present, so the handler defers. In a container the app would run on Kestrel, nothing
  would be present to defer to, and the same `AddNegotiate()` line would have to do the work
  itself for the first time. That is a behaviour change in the authentication path, not a
  configuration change.
- **PathBase.** Not IIS's job here. `Program.cs:354-358` reads `Application:PathBase` and calls
  `UsePathBase` in application code. A container keeps this unchanged.
- **App pool identity.** This is how the app gets a domain identity for ambient AD queries
  (`Services/ADDirectorySearchService.cs`) and for the loaded user profile that the credential
  locker needs. In a container the equivalent is a gMSA; see section 3.
- **TLS, bindings, the parent site.** Host concerns that any ingress can provide.
- **Nothing else.** There is no ISAPI filter, no IIS rewrite module, no IIS-specific middleware.
  A single small `web.config` is the whole IIS surface.

So: **IIS is the current host, not an architectural dependency - with the single exception of
Windows Authentication.** A Kestrel-in-container deployment is equivalent for everything except
that one thing, and inequivalent for that one thing in a way that section 3 has to deal with.

One caveat that is easy to miss and expensive to discover late: the deploy scripts do not merely
enable Windows auth, they **remove the Negotiate provider**, with the comment on `deploy.ps1:381`
saying why - "Remove Negotiate provider - forces NTLM which works without SPN registration". The
app is therefore authenticating browsers with raw NTLM today, deliberately, to avoid needing an
SPN. Kestrel cannot reproduce that. Microsoft's Windows authentication guidance is explicit:
"Kestrel requires the `Negotiate` header prefix, it doesn't support directly specifying `NTLM`
in the request or response auth headers. NTLM is supported in Kestrel, but it must be sent as
`Negotiate`." Under `Negotiate`, Kerberos is attempted first, which needs an SPN. Containerizing
therefore forces the SPN work the current deployment was designed to avoid.

---

## 3. How does Windows Authentication survive containerization?

This is the crux, and it has two halves that are often conflated. The container needs a domain
identity (solvable, with effort), **and** the app needs its Delinea bootstrap credential
(not solvable by container configuration at all).

### 3a. The domain identity: gMSA and a credential spec

Microsoft's position is unambiguous: "Windows containers cannot be domain joined, but many
Windows applications that run in Windows containers still need AD Authentication. To use AD
Authentication, you can configure a Windows container to run with a group Managed Service
Account (gMSA)" (`manage-serviceaccounts`). The machinery is real and supported on Windows
Server 2019, 2022 and 2025. What it requires:

- A **KDS root key** in the forest (one per forest, ever - creating a second breaks existing
  gMSAs after the next password rotation).
- A **gMSA** with `DnsHostName` and `PrincipalsAllowedToRetrieveManagedPassword`, created by a
  Domain Admin or a delegate holding *Create msDS-GroupManagedServiceAccount objects*.
- **SPNs, created by hand.** The doc states: "since containers don't automatically register any
  Service Principal Names (SPN), you will need to manually create at least a host SPN for your
  gMSA account", and that if users reach the site at a name other than the gMSA name, an
  `http/<that name>` SPN is needed too. Given that the deploy scripts currently avoid SPNs
  entirely (section 2), this is new domain work, not a migration of existing domain work.
- **Either** a domain-joined container host that is a member of the gMSA's authorized group,
  **or** a non-domain-joined host with a `ccg.exe` plug-in. The second path is newer and removes
  the domain-join requirement, but Microsoft states outright: "Windows does not currently offer
  a built-in, default plug-in." Without a third-party or self-written plug-in, the host must be
  domain-joined - which quietly removes most of the "deploy anywhere" appeal.
- A **credential spec** JSON, generated by the `CredentialSpec` PowerShell module on a
  domain-joined machine and placed in the runtime's credential-spec directory (for Docker,
  `C:\ProgramData\Docker\CredentialSpecs`), then selected per container at run time.

Kubernetes supports this too, via its own gMSA configuration, which is the only reason the
"orchestrate it later" story is not immediately dead.

**Operator experience when it does not work.** This matters more than the happy path, because
it determines whether a bad deploy is loud or quiet. Three distinct failure shapes:

1. **gMSA not retrievable** (host not in the authorized group, KDS key not replicated, wrong
   credential spec). The container fails to start, or starts with no domain identity. `nltest` and
   `Test-ADServiceAccount` are the documented diagnostics. This is the loud, good failure.
2. **gMSA works but no SPN.** The browser sends `Negotiate`, Kerberos fails for want of an SPN,
   and the client falls back to NTLM inside the `Negotiate` wrapper. On Kestrel that fallback
   may succeed - meaning the deployment silently works today and breaks later when someone
   tightens NTLM, or works for domain clients and not for others. This is the quiet, bad
   failure, and it is the one this app is most exposed to because it has never had an SPN.
3. **Authentication fails outright at runtime.** Every endpoint in this app is behind
   `.RequireAuthorization()` (`Program.cs:375`), and anonymous authentication is disabled in the
   IIS configuration the scripts write (`deploy.ps1:392-394`). There is no configured anonymous
   fallback, so the answer to "does everyone get a 401, or a silent fallback to anonymous?" is
   **everyone gets a 401** - the app is not reachable, rather than reachable and unprotected.
   That is the correct failure direction and worth stating plainly, because it means a botched
   container deploy is an outage, not a security incident.

There is a fourth shape worth naming because it is genuinely dangerous. `GroupAuthorizationHandler`
authorizes on group membership carried in the Windows token. If the container's identity resolves
but group claims arrive thin or empty - a plausible outcome of a partially-working Kerberos setup -
users authenticate and are then denied by policy. That reads to an operator as "the app broke
permissions", not "the container's identity is wrong", and will burn a debugging session. Any
container acceptance test has to assert on a specific group-gated page, not merely on "the login
worked".

### 3b. The actual blocker: the Delinea bootstrap credential

Browser authentication is the half everyone anticipates. This is the half that stops the project.

`Services/DelineaService.cs:28-46` obtains its API username and key from
`CredentialManagerService.ReadCredential(_credentialTarget)`, and
`Services/CredentialManagerService.cs:11-12` implements that with the WinRT
`PasswordVault`. Microsoft's reference for that class states: "Lockers are specific to a
user." The repo corroborates the profile dependency independently - both deploy paths set
`processModel.loadUserProfile = $true` (`deploy.ps1:287`,
`tools/Install-ExchangeAdminWeb.ps1:595`), which exists precisely so the app pool identity has a
profile for that locker to live in.

Now put that in a container:

- A container starts from an image. An image has no user profile and no credential locker for a
  gMSA identity that did not exist when the image was built.
- The credential cannot be baked into the image. Doing so would put a plaintext privileged
  credential into a distributable layer, which the Constitution forbids in terms
  (`docs/ProjectConstitution.md` "Credential Isolation": every privileged credential comes from
  the PAM solution and is "never stored as plaintext in `appsettings.json` or other config
  files"; and "Never log secret values ... or raw PAM/Secret Server auth responses"). An image
  layer is strictly worse than a config file, because it is copied to every host that pulls it.
- It cannot be seeded at container start by any mechanism the app has today, and seeding it by
  hand into every container instance defeats the entire point of the exercise.

The consequence is total, not partial. With no bootstrap credential, `DelineaService` blanks its
credentials and fails closed, so **no module gets a secret**: not the 12 services that build a
`PSCredential` for AD from a Delinea secret, not the Graph modules, not the Exchange Online
certificate lookup path's configuration. A container of this app would start, authenticate
browsers if the gMSA work were done, render its navigation, and then fail on every operation that
touches a backend.

This is an **application change gated on an owner decision**, not a packaging problem. The
Constitution anticipates it: "The PAM backend is a deployment choice, not a hardcoded
assumption ... a future deployment may add another (e.g. CyberArk) or, at minimum, a
Windows-protected/encrypted store. Do not build a new PAM integration speculatively." That last
clause is the operative one. Replacing the credential-locker seam is exactly the kind of change
the Constitution says not to do speculatively, so it needs an explicit owner ruling first (open
question 1) - and until that ruling exists, no container plan can be written that is honest
about being executable.

---

## 4. Does on-prem AD querying work from the container?

**The ambient path: yes, with a working gMSA. The credentialed path: no, for the reason in 3b.
And both need the RSAT module inside the image, which is the biggest unverified item here.**

Three separate requirements, all of which must hold:

1. **A domain identity for the ambient path.** `Services/ADDirectorySearchService.cs` passes no
   `-Credential`; it runs `Get-ADForest` and queries the global catalog on port 3268 as whoever
   the process is. With a correctly configured gMSA the container's processes running as Network
   Service or Local System authenticate to the domain as the gMSA - Microsoft's own summary of
   the ccg.exe flow ends "Applications running as Network Service or Local System in the
   container can now authenticate and access domain resources". So the gMSA is what buys the
   ambient path. That identity must then be granted the same directory read rights the current
   app pool account holds, in the forest, discovered at runtime rather than configured - the app
   already discovers its global catalog from `Get-ADForest` rather than naming one, and a
   container must not become the reason that changes.
2. **The RSAT `ActiveDirectory` module inside the image.** 15 source files import it by name.
   It is not a NuGet package and not in this repo; on the current deploy host it is the RSAT
   module at `C:\Windows\system32\WindowsPowerShell\v1.0\Modules\ActiveDirectory`. Getting it
   into a Server Core image means `Install-WindowsFeature RSAT-AD-PowerShell` at build time.
   **Whether that succeeds in a Server Core container image was not verified** - see
   Assumptions. It is the single highest-risk unknown in the technical plan, because if it
   fails the fallback is copying Microsoft-licensed binaries into an image, which is both
   fragile and a licensing question rather than an engineering one.
   A related risk, also unverified: the module lives under the *Windows PowerShell* module path,
   while the app hosts a PowerShell 7 SDK runspace. That combination works on the current host.
   Whether it works in a Server Core container - and in particular whether it silently depends
   on the Windows PowerShell compatibility layer, which would drag WinRM into the image - has
   not been established.
3. **The Delinea-sourced `PSCredential` for the credentialed path.** Blocked by 3b. This is not
   a corner: Group Management, Self-Service Groups, Protected Principals, Conference Rooms, AD
   Attribute Editor, Emergency Disable, Licensing Updates, BitLocker, Comms 10k, DHCP
   Authorization, Account Lockout Remediation and `ExchangeServiceBase` all build one.

So the honest statement is: **a gMSA makes directory *search* work and does nothing at all for
directory *writes*, because the writes run on a credential the container cannot obtain.**

The Exchange Online path has the same shape for the same reason plus one of its own:
`Services/ExoConnectionPool.cs:507-529` reads the certificate from
`X509Store(StoreName.My, StoreLocation.LocalMachine)`. A container has its own empty machine
store, so the certificate and its private key must be mounted or injected at run time and ACLed
to the container identity - reproducing, in a new mechanism, the ACL step the deploy scripts
perform today at `deploy.ps1:323` and `tools/Install-ExchangeAdminWeb.ps1:621-639`.

---

## 5. What about the shared SQLite config database?

**The repo has already ruled out the only way a container could reach it, and the rule is in
code, not in a doc.**

`Services/Storage/ConfigStorePath.cs:38-43` refuses a UNC path outright, with the reason in the
exception message: "The shared config database must be a local file on this server - SQLite's
locking is not reliable over a network share." Lines 49-53 additionally refuse anything not
fully qualified, so that two instances cannot believe they share one file while opening two.
`Program.cs:50-64` treats a failure here as fatal at startup. This is Architectural Invariant 2
enforced in code.

What that means for a container, step by step:

- A container cannot bind-mount a UNC path directly. The supported mechanism is **SMB global
  mapping**: the host runs `New-SmbGlobalMapping -RemotePath \\server\share -Credential $creds
  -LocalPath G:` and then bind-mounts a directory on `G:` into the container
  (`persistent-storage`). Inside the container the path looks local, so
  `ConfigStorePath.Resolve` would accept it.
- **That acceptance is a false pass.** The code's stated reason for refusing UNC is SQLite
  locking over a network share. SMB global mapping does not change the transport; it changes how
  the path is spelled. A container reaching the shared database this way would be doing exactly
  what the refusal exists to prevent, with the safety check silently satisfied. That is worse
  than being blocked, and any container plan must treat it as a defect to design around rather
  than a convenience.
- Two further documented limits bite here. SMB global mapping "does not support DFS Namespaces
  (DFSN) folders" - so if the destination's file path is a DFS namespace, the mechanism is
  simply unavailable. And "when using SMB global mapping for containers, all users on the
  container host can access the remote share", which widens access to the configuration
  database beyond the app's identity.
- Invariant 2's "dev and prod open the same file" does not survive the move in any case. Two
  containers on two hosts are not two app pools on one server. The invariant was decided
  (2026-09-04) for co-located instances on a single machine; a container deployment "elsewhere"
  either abandons the shared-database property - which changes the operator model, since a
  setting saved in one place is currently live in the other at once - or runs SQLite WAL over
  SMB from two hosts, which the code says is unsound. **Neither branch is acceptable without an
  owner ruling** (open question 4).
- Separately, three things under the content root are per-instance and deploy-excluded by
  invariant 3: `config/exchangeadmin-jobs.db` (`Program.cs:82`),
  `config/exchangeadmin-usage.db` (`Program.cs:91`) and `logs/` (`Program.cs:28`). In a
  container these live in the ephemeral scratch space, which Microsoft describes as discarded on
  stop: "When a container instance is stopped, all changes that occurred in the scratch space
  are thrown away." Job state and logs vanishing on every restart is a behaviour change that
  needs a deliberate volume for each, and the jobs database in particular holds durable
  server-side batches. Losing it silently would violate the Constitution's "Do not write durable
  state into locations that deployment or log pruning scripts delete" in spirit if not in
  letter.

---

## 6. Verdict

**Not worth doing. Recommend against containerizing, and reach the owner's actual goal by
finishing the installer instead.**

The reasoning, compressed:

- The goal is "deploy elsewhere rapidly". A Windows container delivers that only if the
  destination host runs a matching Windows Server build, holds a container runtime, is
  domain-joined or carries a ccg.exe plug-in that Microsoft does not ship, and has a gMSA with
  hand-made SPNs prepared in the forest. That is not a faster deployment than running an
  installer; it is a slower one with more moving parts and a new class of silent failure.
- The one genuinely container-shaped benefit - an immutable artifact - is undercut by the image
  being multi-gigabyte, version-locked to the host OS, and unable to contain the credential it
  needs to function.
- The blocking item (3b) is an application change to the credential bootstrap that the
  Constitution explicitly says not to undertake speculatively. Spending it on containerization
  spends it on the wrong problem: the same change would benefit the installer route too, at
  which point the container adds nothing the installer lacks.

**Effort, in this repo's session unit** (one slice, one session, per the Token Budget protocol).
These are estimates for the container route and are offered so the comparison is concrete, not
as a recommendation to spend them:

| Work | Sessions |
| --- | --- |
| Replace the WinRT credential-locker bootstrap with a container-viable seam, plus tests | 3 to 4 |
| Build and prove a Server Core image with RSAT and the EXO module (assumes RSAT installs; add 2 or more if it does not) | 2 to 3 |
| gMSA, credential spec, SPNs, and proving Kestrel Negotiate end to end | 2 to 3 |
| Certificate and shared-database plumbing, plus volumes for jobs/usage/logs | 2 |
| Reworking `deploy-pipeline.ps1` / `promote-dev-to-prod.ps1` around images instead of robocopy, preserving invariant 3 and `-PlanOnly` | 3 |
| Documentation, decisions entries, manual acceptance | 1 to 2 |
| **Total** | **13 to 17** |

Against that, the cheaper route below is estimated at **3 to 4 sessions** and has no
prerequisite the owner must arrange in Active Directory.

---

## The cheaper route to "deploy elsewhere rapidly"

`.agents/repo-guidance.md` Architectural Invariant 1 already says
`tools/Install-ExchangeAdminWeb.ps1` "is environment-neutral and standalone. Never couple it to
`deploy.ps1` or ADI-specific configuration." That script is the repo's existing answer to the
owner's actual question, and it is close. Its own header describes a fresh install that builds
from source, creates and configures the app pool and web application, generates
`appsettings.json`, seeds config, and sets ACLs (`tools/Install-ExchangeAdminWeb.ps1:11-17`).

What it does **not** do is establish the host prerequisites, and that is precisely the gap
between "run one script" and "spend a day figuring out why the app starts but every module
fails". Read its closing "Next steps" (lines 752-756): the four things it tells the operator to
do next are all in-app configuration. Nothing tells them the host needs RSAT, the
`ExchangeOnlineManagement` module, the .NET hosting bundle, the EXO certificate in
`LocalMachine\My` with its private key readable, and the Delinea bootstrap credential in the app
pool identity's credential locker. Every one of those is a silent runtime failure if missing, and
every one of them is knowable by the script.

**Proposed slices, one session each. Nothing here is approved; this is the shape of the
alternative, offered so the owner can compare it against 13 to 17 sessions.**

- **Slice 1 - Prerequisite preflight.** A `-PlanOnly`-honouring phase at the top of the
  installer that checks, reports and refuses: Windows PowerShell 5.1 availability and the `IIS:`
  drive; the ASP.NET Core hosting bundle / `AspNetCoreModuleV2`; the `ActiveDirectory` module;
  the `ExchangeOnlineManagement` module; `sqlite3.exe` on PATH for the backup path; the EXO
  certificate present with a readable private key; and the Delinea credential resolvable under
  the configured target. Each check fails closed through `Write-Fail`, in line with the PowerShell
  error model in `.agents/repo-guidance.md`. Pester coverage in `tests/ps/`.
- **Slice 2 - Prerequisite remediation, opt-in.** For the checks that can be fixed
  non-interactively, an explicit switch that installs them (RSAT feature, gallery module) and
  reports rather than silently acting. Must honour `-PlanOnly`. Must not install anything by
  default: an installer that reaches out to the gallery without being asked is not
  environment-neutral.
- **Slice 3 - A written bring-up runbook** covering the parts a script cannot do: creating the
  service account, creating the EXO app registration and certificate, creating the Delinea
  secret and seeding the bootstrap credential, and choosing `ConfigStorePath`. This is the
  artifact that turns "deploy elsewhere" from tribal knowledge into a document, and it is the
  single highest-value item in this list.
- **Slice 4, optional - `-PlanOnly` parity for `deploy.ps1`.** `tools/deploy-pipeline.ps1:82`
  currently warns "deploy.ps1 has no native plan mode yet; -PlanOnly skips the dev deploy
  entirely", which means the pipeline's dry run does not actually dry-run the deploy. Closing
  that is independently worth doing and is required by `.agents/repo-guidance.md` Architectural
  Invariant 4 and by the Constitution's "Do not change production deployment behavior without
  dry-run support".

Slices 1 to 3 give the owner a host that can be stood up from a checkout plus a runbook, on any
Windows Server with IIS, with no forest changes, no gMSA, no SPNs, no image registry and no
credential-bootstrap redesign. That is the outcome the queue item asked for.

**Note on ordering.** Slices 1 and 2 touch `.ps1` files and slice 4 touches deployment
behaviour, so all of them fall under the Constitution's Planning Rules ("deployment scripts")
and need their own approved plan before implementation. This document is not that plan; it is
the recommendation that one be written.

---

## Versioning

If the cheaper route is approved: `tools/Install-ExchangeAdminWeb.ps1`, `deploy.ps1` and
`tools/deploy-pipeline.ps1` are shared infrastructure, not module-scoped behaviour.
`docs/ProjectConstitution.md` "Deployment And Versioning" says "Shared infrastructure changes
bump the base app version", so each landing slice bumps `<VersionPrefix>`, `AssemblyVersion` and
`FileVersion` in `ExchangeAdminWeb.csproj`. No module version in `Modules/ModuleCatalog.cs`
changes, because no module's behaviour changes.

If the container route were ever approved, the same rule applies and applies more strongly: a
change to how the app bootstraps its credentials is shared infrastructure by any reading.

This document alone is docs-only and bumps nothing.

---

## Verification

For whichever route is approved. Commands are this repo's real ones from
`.agents/repo-guidance.md`, not generic ones.

- This document as it stands: `git diff --check HEAD`. Docs-only, no behaviour.
- Any `.ps1` slice: `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps`.
- Any `.cs` slice (the credential-bootstrap change, if it ever happens):
  `dotnet build ExchangeAdminWeb.slnx -c Release`, then `dotnet test ExchangeAdminWeb.slnx`,
  then `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`. Always target the
  `.slnx`; a bare `dotnet test` from the repo root runs zero tests.
- New Pester coverage must be proved non-vacuous: break the prerequisite the check guards,
  confirm the test fails, restore, confirm it passes. Note the recorded trap in memory for
  mutation probes - a `Copy-Item` restore keeps the old mtime and MSBuild then skips the
  rebuild, so touch the file after restoring.

---

## Manual acceptance checklist

Not runnable by automation; for the owner or an operator. Applies to the cheaper route.

1. On a Windows Server with IIS and **none** of the prerequisites installed, run
   `tools\Install-ExchangeAdminWeb.ps1 -PlanOnly`. Confirm every missing prerequisite is named,
   and that no change was made.
2. Run it without `-PlanOnly` and confirm it refuses rather than producing a half-installed app.
3. Install the prerequisites per the runbook, re-run, and confirm the app starts.
4. Sign in as a member of an admin group and confirm a group-gated page renders - not merely
   that sign-in succeeded. (Thin group claims authenticate and then deny; the distinction is the
   point of this step.)
5. Exercise one Delinea-backed module end to end and confirm a real backend result, proving the
   credential bootstrap works on the new host.
6. Confirm `ConfigStore:Path` behaviour: with the key absent the instance uses its own
   `config/exchangeadmin.db`; with it set to a missing file the app refuses to start.
7. Re-run the installer as an update and confirm `appsettings.json`, `config/` and `logs/` are
   preserved (Architectural Invariant 3).

---

## Assumptions

Labelled because they were not verified, and each would change the plan if wrong.

1. **`Install-WindowsFeature RSAT-AD-PowerShell` succeeds in a `windowsservercore` container
   image.** Not verified - running Docker was out of scope. This is the highest-risk unknown in
   the container route. If it fails, the route needs a licensing answer, not an engineering one.
2. **The RSAT `ActiveDirectory` module loads natively into a hosted PowerShell 7 SDK runspace
   inside a container, without the Windows PowerShell compatibility layer.** It demonstrably
   works on the current host; the container case, and whether it would drag WinRM into the
   image, was not tested.
3. **Image size.** "Multi-gigabyte" is inferred from Server Core plus runtime plus RSAT plus the
   EXO module. No image was built.
4. **Container runtime availability on the current host** was not checked, per the task's
   constraint not to run Docker or change the host.
5. **PasswordVault behaviour under a gMSA identity in a container** was not tested. The
   conclusion in 3b does not depend on it: even if the API were callable, the locker would be
   empty in a fresh container and there is no supported way to populate it at start.
6. **Licensing.** Windows Server container licensing for any specific destination was not
   investigated and is an owner question.

---

## Open questions for the owner

Each answerable in one line. Numbered for reference.

1. **Is replacing the Delinea bootstrap credential seam (today: the Windows per-user credential
   locker) on the table at all?** If no, the container route is closed permanently and this
   document is the final answer; if yes, it is a separate plan and should be justified on its own
   merits rather than as container plumbing.
2. **Approve the cheaper route** - a prerequisite preflight in
   `tools/Install-ExchangeAdminWeb.ps1` plus a bring-up runbook, 3 to 4 sessions - **or shelve
   queue item 6 entirely?**
3. **Where is "elsewhere"?** Another server in the same forest, a different forest, a customer
   site, or a cloud host with no line of sight to on-prem AD? A destination that cannot reach a
   domain controller cannot run this app in any packaging, container or not.
4. **If two instances ever run on two different hosts, do they still share one config database?**
   Invariant 2 assumes co-location; the answer decides whether the shared-database property
   survives a move at all.
5. **Must a new deployment reuse this Exchange Online app registration and certificate, or get
   its own?** This changes the runbook and the certificate-provisioning step materially.
6. **Is registering SPNs in the forest acceptable?** The current deployment deliberately avoids
   them (`deploy.ps1:381`); any Kestrel-hosted deployment, containerized or not, needs them.
7. **Should slice 4 (`-PlanOnly` parity for `deploy.ps1`) be pulled out and done on its own
   merits?** It is an existing invariant-4 gap that this investigation surfaced and that does not
   depend on any answer above.

---

## Sources

All verified 2026-09-18.

- Create gMSAs for Windows containers -
  https://learn.microsoft.com/en-us/virtualization/windowscontainers/manage-containers/manage-serviceaccounts
- Windows container version compatibility -
  https://learn.microsoft.com/en-us/virtualization/windowscontainers/deploy-containers/version-compatibility
- Persistent storage in containers (SMB global mapping) -
  https://learn.microsoft.com/en-us/virtualization/windowscontainers/manage-containers/persistent-storage
- Container storage overview (scratch space) -
  https://learn.microsoft.com/en-us/virtualization/windowscontainers/manage-containers/container-storage
- Configure Windows Authentication in ASP.NET Core (Kestrel, Kerberos vs NTLM) -
  https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0
- Microsoft.AspNetCore.Authentication.Negotiate namespace (PostConfigureNegotiateOptions) -
  https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.negotiate?view=aspnetcore-10.0
- Run an ASP.NET Core app in Docker containers -
  https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images?view=aspnetcore-10.0
- PasswordVault class ("Lockers are specific to a user") -
  https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.passwordvault
- MCR dotnet/aspnet tag list (Windows 10.0 tags) -
  https://mcr.microsoft.com/v2/dotnet/aspnet/tags/list
