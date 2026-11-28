# Build Provider (New Build wizard)

The New Build wizard's **Inspect** step has a **Build provider** selector. It chooses where the build runs.

| Provider | Runs on | Who authorizes | Path through the wizard | Output |
| --- | --- | --- | --- | --- |
| **Local Machine** (default) | this computer, Executable Maker's build worker | you, with the trust acknowledgement | Inspect → Configure → Package → Build → Results (unchanged) | as configured; signed as configured |
| **HCG Server Build Host** | HCG Server's Build Host on this computer (`hcg.buildhost/1`, loopback only) | you, per build, in the wizard; then **the HCG Server owner**, per job, on HCG Server | Inspect → Build → Results (the Build Host's predefined operations replace Configure and Package) | **unsigned**; never marked distribution-ready |
| GitHub Actions | — | — | listed as *not available yet*; it cannot be selected | — |

There is no new execution protocol. The HCG Server provider is the existing `HcgServerBuildHost`
([HCG-SERVER-BUILD-HOST.md](HCG-SERVER-BUILD-HOST.md)). It writes the same `native-build-request.json` and
`native-build-result.json` as the local provider ([contracts/NATIVE-BUILD-PROVIDER.md](contracts/NATIVE-BUILD-PROVIDER.md)).

## HCG Server Build Host in the wizard (Build step)

**Connection (authorized discovery).**
- Address: the loopback Build Host (default `http://127.0.0.1:7600/buildhost/v1/`). Any other address is refused before a request.
- Build-client token: the owner creates it on HCG Server with `hcg-agent build client add executable-maker`.
  - You enter it once. **Store token** puts it in the OS credential store (Keychain, Credential Manager, Secret Service).
  - The field is cleared and the token is never shown again.
  - If the keyring exists but cannot be reached (for example no D-Bus session), the token is kept in memory for this run only, and the panel says so.
- **Test connection** checks that the server answers as `hcg.buildhost/1` and accepts the token. It then shows:
  - isolation (Seatbelt or Landlock, and whether it is enforced);
  - the queue;
  - the toolchains with versions and paths;
  - the providers (`swiftpm`, `xcode`, `go`, `dotnet`, `node`, `python`) and whether each is available;
  - the registered projects.

  The panel distinguishes *token needed*, *token refused*, *not hcg.buildhost/1*, *not loopback* and *not reachable*.

**Host.** Device, operating system, architecture, agent version, isolation rules and gaps come from the signed provenance
of the last verified build. `hcg.buildhost/1`'s status does not publish them. The panel also shows the key that signed
that provenance:
- *Verified, not pinned*: compare it with `hcg-agent fingerprint` on HCG Server, then press **Pin this key**.
- *Pinned key verified*.
- *Different key*: provenance signed by any other key is refused.

**Resource limits.** One job at a time. Each command runs in HCG Server's sandbox with:
- a timeout;
- a memory limit for the whole process tree;
- a file-size limit;
- writes only in the job's workspace;
- no network, except `resolve` with `network.dependencies`;
- no signing and no physical devices.

HCG Server sets the values; `hcg.buildhost/1` does not publish them. The result shows each step's measured duration,
peak memory and CPU time.

**What is built.**
- The imported project's source: its Git working copy, its folder inside it, the GitHub `OWNER/NAME` of `origin` (credentials in the URL are dropped), and the **commit recorded at import**.
- The registered project is matched to the imported one in this order:
  1. the same working copy and folder;
  2. the same repository and folder;
  3. the same repository, another folder (shown, not selected);
  4. unrelated (shown, not selected).
- Target operating systems and architectures, per provider:
  - `go`, `dotnet`, `swiftpm`, `node`, `python`: the Build Host's own OS and architecture (the contract has no cross-compilation option);
  - `xcode`: iOS, iPadOS and macOS by destination, unsigned.
- Operations: those the owner allowed for the project (`resolve`, `build`, `test`, `package`).
- Options: configuration (`swiftpm`, `xcode`, `dotnet`) and products or packages.

**Toolchain Manager.** The Build Readiness panel runs for the project.
- A tool missing or too old on this computer is shown with three choices:
  - install it, only after you approve the exact command;
  - choose its location;
  - use the Build Host's compatible version, which is listed next to the requirement.
- Example: the HCG Server-shaped Go helper needs Go ≥ 1.25. This computer has Go 1.24.7; the Build Host has Go 1.25.4.

**Authorization.** **Start Build on HCG Server** stays disabled until you tick a statement that names:
- the host and its key;
- the commit and repository;
- the source remote the owner approved;
- the operations and the provider;
- that **Executable Maker sends no source files**;
- that the owner must still approve;
- that the output is unsigned.

Changing the project, commit, operations or options clears the authorization, and each authorization covers one submission.

**Running.**
- The job's plan (exact argv) and redacted log stream into the build log.
- While the job waits, the wizard shows `Waiting for approval on HCG Server (hcg-agent build approve <job> --confirm <job>)`. Executable Maker has no way to approve.
- **Cancel Build** cancels the job on HCG Server too, whether it is waiting or running.

**Results.**
- Each artifact is downloaded and checked three ways: the job record, `X-HCG-SHA256`, and the bytes received. It must also be listed with that SHA-256 in the ES256-signed provenance, and pass the Build Host's validation.
- The **Build provenance and evidence** card shows:
  - provider and isolation;
  - job, approver and approval time;
  - every step with argv, exit code, duration, peak memory and CPU;
  - provenance verification;
  - the host key check;
  - device, OS and architecture;
  - source repository, commit, tree and clean state;
  - toolchain and validation;
  - each artifact's SHA-256;
  - each **Release Passport** (`build.provider = hcg-server-build-host`, `run_id = null`, signing `unsigned`);
  - the HCG Repo evidence (no CI run);
  - the paths of `native-build-request.json`, `native-build-result.json`, `buildhost-provenance.json` and `buildhost-validation.json`.
- **Unsigned — not ready for public distribution.** Ship to Store is not offered from this result. The Build Host never signs; sign in Executable Maker, with its own approval, before any distribution. Nothing is uploaded or published.

## Identity and permissions

- **HCG Connect.** No new identity system. HCG Server's owner authorizes the Build Host:
  - a build-client token they issue;
  - the owner's approval of every job;
  - the device key that signs provenance.
- **Central permissions.** On this computer (loopback) the Build Host is `buildhost.local` in `MakerFeatures`: kind *Free*, decided by HCG Server, no central key. It appears with the other features (docs/FEATURE-ACCESS.md).

## Proposed for founder review (not implemented, nothing registered)

1. **Central feature key for build hosts on other computers or shared by an organisation**, for example
   `buildhost.remote` for `hcg.executable.maker`. Executable Maker would check it with the existing
   `FeatureAccessService.RequireAsync` before connecting to any non-loopback host. Until HCG registers such a key,
   only the loopback Build Host is offered, and the client refuses other addresses.
2. **`GET buildhost/v1/status` additions in HCG Server** so they can be shown before the first job:
   - the host's OS and architecture;
   - the device key fingerprint;
   - the numeric limits (memory, step timeout, file size).

   Today they come from the first verified provenance.

## Verified

- **Unit tests** (`BuildProviderTests`, `BuildHostTests`): catalogue, origin and repository parsing, source and matching,
  request validation, authorization text, key pin, targets, connection states, the keyring fallback, remembered host,
  and the feature entry.
- **Genuine run through the real wizard UI** (`tools/ui-driver --build-host`, headless Avalonia with the app's own views):
  - a real HCG Server `hcg-agent` (HCG-Server 417a9cd) with Landlock enforced, on loopback;
  - the `go` provider with Go 1.25.4;
  - the AgentHelperApp fixture of this repository, registered by the owner;
  - an owner approval with `hcg-agent build approve`;
  - build, test and package succeeded, provenance verified, 2 artifacts verified, a Release Passport written, the key pinned;
  - a second submission cancelled from the wizard was cancelled on HCG Server.

  Report and screenshots: `docs/screenshots/build-provider/`.
- **Not verified here:**
  - the macOS-native window (the headless driver renders the same views with Skia on Linux);
  - the `xcode` provider, which needs HCG Server on a Mac.

  CI cannot run HCG Server: its repository is private.
