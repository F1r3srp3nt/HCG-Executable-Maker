# Windows packaging, Authenticode and Ship to Store

HCG Mail for Windows 0.1.0-beta.1 was packaged with Executable Maker's engine at 595e989: x64 and ARM64 installers and
portable ZIPs. To get there, HCG Mail had to copy Maker's self-package script with its own identity hard-coded. It then
wrote its own evidence step (Authenticode, PE machine type, version info) and its own Store draft and passport script.
This page describes the reusable parts that replace those steps.

## Package any Windows app with its own identity

```
HCGExecutableMaker --package-windows --identity app.json --app-dir <already-built folder> --arch x64|arm64 \
    [--formats installer,zip,folder] [--out DIR] [--dist DIR] [--built-with ".NET SDK 10.0.100"] \
    [--provider github-actions --run-id <workflow run ID>]
HCGExecutableMaker --package-windows --identity app.json --source <project folder> [--project src/App/App.csproj] \
    [--targets win-x64,win-arm64]
```

`app.json` holds only the product's own facts. Every field can also be given on the command line (`--name`,
`--publisher`, `--app-id`, `--version`, `--exe`, `--icon`, …):

```json
{ "name": "HCG Mail", "publisher": "Hakmi Capital Group", "applicationId": "com.hakmicapitalgroup.hcgmail",
  "version": "0.1.0-beta.1", "executable": "HCGMail.exe", "icon": "src/HCG.Mail.App/Assets/Icons/hcg-mail.png",
  "buildNumber": "1", "productKey": "hcg-mail", "edition": "windows-desktop", "distName": "HCGMail-{version}-{rid}",
  "store": { "productId": "hcg-mail", "editionId": "windows-desktop", "distribution": "free",
             "releaseNotes": ["Public testing prerelease."] } }
```

- **`--app-dir`** packages a folder that is already built (a `dotnet publish` output, or any folder with the program
  and its files) and builds nothing.
  - The program's PE machine type must match `--arch`. An x86 or wrong-architecture program is refused before
    anything is written.
  - The folder is only read. When signing is approved, the program is signed in a staging copy.
- **`--source`** builds with the same engine as New Build (`dotnet publish` per target), then packages the result.
- **Outputs** (Inno Setup installer, portable ZIP, folder) and the verification checks are unchanged. Each file also
  gets a SHA-256, an entry in `SHA256SUMS.txt` and a Release Passport (below).
- **`--dist`** copies the files under the `distName` pattern (`{name}`, `{version}`, `{rid}`, `{arch}`). The passports
  are renamed with them: a passport always names the file whose bytes it describes.
- **Upgrade identity:** the installer's AppId is derived from the application ID, unchanged from 595e989, so HCG Mail
  0.1.0-beta.1 installations upgrade in place. Nothing of Executable Maker's own name, icon or ID goes into the product.

## Installer bootstrapper and installed program

An Inno Setup `Setup.exe` is a 32-bit **x86** loader on every target. The program it installs is x64 or ARM64. Maker
reports them separately:

- **Installer checks:**
  - *Installer bootstrapper*: the loader's machine type, and "installs for arm64 only (ArchitecturesAllowed=arm64)".
  - *Installed application*: the program's machine type, product version and company, which must match the target.
    An x64 program inside an ARM64 installer fails.
- **Release Passport:** `architecture` is the installed program's. `windows.bootstrapper.machine` records the
  loader's x86 separately.
- **Ship to Store:** a Setup.exe's architecture comes from its passport, never from the loader's header. Without a
  passport the Store is told no architecture, and the note says why. A plain `.exe` and the programs in a portable
  ZIP report their own machine types.

## Signing Doctor for Windows (Authenticode)

```
HCGExecutableMaker --windows-signing-doctor <file.exe | portable.zip | folder>
```

The Signing Doctor reads the signature from the file itself, the same way on Windows, macOS and Linux:

- **Digest:** the PE image digest is recomputed (the whole file except the checksum, the certificate-table entry and
  the signature) and compared with the signed digest. A file changed after signing fails.
- **Signature:** the signer's signature over the signed attributes is checked with its certificate.
- **Timestamp:** an RFC 3161 timestamp or a legacy countersignature is checked against the signature it stamps, and
  its time and authority are shown.
- **Chain:** the certificate chain is built for code signing with this computer's trust store. Trust is reported
  separately from integrity.
- **On Windows:** Windows' own verdict (`Get-AuthenticodeSignature`, WinVerifyTrust) is shown next to Maker's.

| Status | Meaning | HCG Store signing |
|---|---|---|
| `unsigned` | no Authenticode signature | `unsigned` |
| `signed` | intact, timestamp verified, chain trusted | `signed` |
| `signed-untrusted` | intact, but the certificate is not trusted here (self-signed, unknown root) | `invalid` |
| `invalid` | changed after signing, forged, or unreadable | `invalid` |

Nothing is ever reported as better than the check showed. The evidence sent to the Store names files only, never this
computer's paths.

## Authenticode signing (explicit approval, Windows)

```
HCGExecutableMaker --authenticode-sign <file.exe> --thumbprint <certificate SHA-1> --timestamp-url <RFC 3161 URL> \
    --approve <SHA-256 of the file>
HCGExecutableMaker --package-windows … --authenticode-thumbprint <SHA-1> --timestamp-url <URL> --approve-signing
```

- **Tool and certificate:** signing uses `signtool` with SHA-256 and an RFC 3161 timestamp. It uses the certificate
  you name by thumbprint, from your own certificate store. Maker holds no keys and creates or imports no
  certificates.
- **Approval:** without the approval, nothing is signed. `--approve` must be the file's current SHA-256, at least its
  first 12 characters. `--approve-signing` approves signing this run's program and Setup.exe.
- **Verification afterwards:** the result is checked again. A signature without a verified timestamp is reported as a
  failure, because it would lapse when the certificate expires.

## Release Passport for Windows files

`<file>.release-passport.json` (HCG Release Passport v1, build half) is written next to each installer and ZIP, by
`--package-windows` and by every Windows .NET build in New Build. It holds:

- the exact SHA-256 and size;
- the product's name, ID, version and build number, product key and edition;
- the architecture installed;
- `windows.installed_program` (machine, subsystem, product and file version, company) and `windows.bootstrapper`;
- the source commit as Git states it;
- the build provider and run ID: `executable-maker-local` by default; inside a workflow run, pass
  `--provider github-actions --run-id ${{ github.run_id }}` (the real run's ID, never an invented one);
- the toolchain (`--built-with`, Executable Maker, Inno Setup);
- the artifact checks;
- `code_signing` from Authenticode, for the file and each program: status, signer, thumbprint, digest, chain,
  timestamp, and Windows' verdict when read on Windows.

`repository_signing`, channel, access, approvals and publication are left to the distribution authority.

## Store submission manifest and the deployed HCG Store

With `--store-product` (or `store` in `app.json`), `--package-windows` also writes `store-submission.json`
(`hcg.store-submission` v1):

- **`release`** is the exact body of `POST /api/v1/publishing/releases`. Eligibility is always `private`, so HCG Store
  creates a private draft.
- **Each artifact's `declare`** is the exact body of `POST /api/v1/publishing/releases/:id/artifacts`. It holds only
  the fields the deployed HCG Store accepts. Its schema is strict, so any other field is refused.
- **Each artifact's `provenance`** (source commit, provider, toolchain, validation report) is kept apart. It is sent
  where the Store accepts it, and it always stays in the passport.

```
HCGExecutableMaker --ship-to-store --manifest dist/store-submission.json [--sign-in]
HCGExecutableMaker --ship-to-store --store-status
```

`--manifest` creates the private draft after checking each file's SHA-256 and size against the manifest again. A file
that changed since stops everything. `--store-status` reads the server's public health and asks the publishing API
for this account's permissions. It changes nothing.

**The deployed HCG Store**, as recorded by HCG Store's own deploy workflow (26 Nov 2026):

- `store.hakmicapitalgroup.com` and `hcg-store.fly.dev` serve HCG Store 835ac1a, which reports `mode: production` and
  `hcgConnect: configured`. Payments and protected downloads are disabled.
- Its `fly.toml` has `HCG_STORE_PUBLISHING = "disabled"`, so every `/api/v1/publishing/*` route answers 404
  `publishing_disabled`. Maker reports that as *Publishing is not enabled on this HCG Store*, not as an error of the
  file.
- The two newer deploys (3749fe6 Ship to Store v2, 13d15c6) failed before deploying: Fly.io refused the repository's
  `FLY_API_TOKEN` (401 Unauthorized). Renewing that secret and enabling publishing (phase 3 in HCG Store's
  `docs/PRODUCTION-DEPLOY.md`) are the owner's decisions.
- **Provenance against the deployed server:** 835ac1a's artifact schema has no build provenance. It answers
  `400 invalid_request "request: Unrecognized keys: "sourceCommit", "buildProvider", "toolchain", "validationReport""`;
  that text was reproduced with the Store's own zod 4.6.7. On that exact answer, Maker declares the file again
  without those four fields and says so. Any other refusal still fails.

## For HCG Mail

`windows-prerelease.yml` can replace three things with Executable Maker commands:

- the copied `package-windows.cs.template`, with `--package-windows --source … --identity app.json` (or
  `--app-dir` for its own publish output);
- the PowerShell evidence step's Authenticode, PE-machine and version checks, with the passports and checks above;
- the hand-built `store-release-draft.json`, with `store-submission.json`.

The hand-built draft put `sourceCommit`, `buildProvider`, `toolchain`, `validationReport` and `replaces` in each
artifact. The deployed Store would refuse that body.

HCG Mail's runtime checks (Credential Manager, TLS, window rendering) and the installer test stay HCG Mail's. Maker's
passport records `application_launch` and `runtime_verification` as `not-recorded`, never as passed.
