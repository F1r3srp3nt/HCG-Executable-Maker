# Build hosts (proposal for HCG Server review)

Status: **proposal**. Only the local host exists (`LocalBuildHost`, `src/HCG.ExecutableMaker.Core/BuildHosts.cs`).
Nothing here is deployed, registered or configured, and no HCG Server contract is assumed.

## Why

Some HCG apps can only be built on the owner's MacBook (Xcode 27.2; private sources whose CI cannot run). The goal is
to make that Mac an **authorized HCG build host**: HCG Server could ask it to build a pinned commit and receive the
exact artifact with its provenance, without GitHub Actions.

## Interface (implemented)

`IBuildHost.Describe()` reports the host's id, kind, label, operating system, architecture and toolchains.
`IBuildHost.CannotBuild(platform)` says why a host cannot build for a platform. Builds still run through Maker's
existing engines; a host decides only *where* they run.

## Remote host (proposed, not implemented)

1. **Registration.** The owner signs in with HCG Connect in Maker on the Mac and approves "Use this Mac as a build host".
   HCG Server records a host id and a public key generated on the Mac (Keychain, non-exportable). There is no shared
   secret and no service-role key.
2. **Job.** HCG Server offers a job:
   - repository name, the full commit, scheme;
   - package id and version, minimum iOS, the intended channel.

   No source, credentials or tokens are sent. The Mac uses its own local checkout or the owner's git credentials.
3. **Consent.** Maker shows the job. The owner approves each job, or a standing rule they set per repository.
4. **Build.** The steps are exactly the local Unleashify path: pinned checkout → archive → package → validate →
   lifecycle → handoff.
5. **Result.** Maker sends the .deb, its SHA-256, `provenance.json` (`local_build` with `host = <host id>`) and
   `VALIDATION.txt`. The request is signed with the host key. HCG Server stores the result as a candidate. Publishing to
   HCG Repo stays the owner's approval of that SHA-256.

Constraints: no Apple account credentials ever leave the Mac; logs are redacted (UDID, ECID, serials, e-mail, home
paths); a job never revokes certificates, registers devices or changes signing without the owner's explicit approval.

Open questions for HCG Server: the host registration endpoint and key format; the job queue (poll vs push); the
artifact upload (presigned storage like Ship to Store's upload sessions?); the audit trail.
