# Toolchains: discovery, setup and readiness

HCG Executable Maker never installs SDKs, accepts licences or changes machine-wide settings (`xcode-select`,
`JAVA_HOME`, `ANDROID_HOME`, shell profiles). Choices you make in HCG apply to HCG only.

## States

| State | Meaning |
|---|---|
| **Ready** | Found **and** validated: a version probe ran or the SDK structure checked out. An executable merely existing is never Ready. |
| **Not found** | Not in any location HCG looks in (see each tool below). |
| **Invalid path** | A saved or configured path does not contain the tool (for example, a folder that is not a JDK). |
| **Incompatible version** | Works, but the version does not fit (for example, JDK 21 with a Gradle 8.4 project, or JDK 11 with AGP 8). |
| **Setup required** | Installed, but a step of yours is needed: select Xcode, first launch, licence, or an SDK component. |
| **Not needed for this build** | In a project's readiness: this build does not use the tool. |
| **Other host** | The tool belongs to another OS (Windows tools on a Mac). Nothing to fix on this computer. |

## Xcode (Toolchains › Xcode)

- **Discovery:**
  - Xcode apps in `/Applications` and `~/Applications`, plus Spotlight;
  - *Browse…* for any other location;
  - aliases (symlinks) of the same Xcode are listed once.
- **Diagnosis:**
  - not installed;
  - installed but not selected (the Command Line Tools are active);
  - licence not accepted;
  - first-launch setup incomplete;
  - SDKs missing;
  - validation failed (with the tool's own error).
- **Validation:** `xcodebuild -version`, `-checkFirstLaunchStatus` and `-showsdks -json`.
- **Selection:**
  - *Select Xcode* stores an HCG-only developer directory, passed as `DEVELOPER_DIR` to every probe and build process;
  - `xcode-select` is never run;
  - the machine-wide command is shown for reference only.
- **iOS rows follow the diagnosis.** With the Command Line Tools selected:
  - the iOS SDK shows **Setup required: select Xcode**, not "Missing";
  - provisioning profiles show as not needed for Simulator builds.

## Java (Toolchains › Java)

Candidates, in order:

1. the JDK saved in HCG (Browse… / Verify);
2. `JAVA_HOME`;
3. Android Studio's bundled JDK (`Android Studio.app/Contents/jbr/Contents/Home`, and the older `jre` layouts);
4. `/usr/libexec/java_home -V`;
5. `/Library/Java/JavaVirtualMachines` and `~/Library/Java/JavaVirtualMachines`;
6. Homebrew (`/opt/homebrew/opt/openjdk*`, `/usr/local/opt/openjdk*`);
7. SDKMAN;
8. the system JVM folders on Linux and Windows;
9. `java` on PATH (last).

None of these needs the login shell, so HCG finds the same JDK when it is opened from Finder.

Each candidate is **run**: `java -XshowSettings:properties -version`, with stdout and stderr both read and JAVA_HOME
pointed at the candidate. HCG rejects, with the reason:
- macOS's `/usr/bin/java` stub without a JDK behind it ("Unable to locate a Java Runtime");
- broken shims (asdf, jenv, SDKMAN) that exit with an error or print no version;
- runtimes without `javac`.

A JDK saved in HCG always wins. If it stops working, it is reported as **Invalid path** and never silently replaced.

**Compatibility is checked per project:**
- The JDK must be at least the AGP's minimum: AGP 8 and 9 need 17; AGP 7 needs 11.
- The project's Gradle wrapper must be able to run on the JDK. The data comes from Gradle's Java compatibility
  matrix: JDK 21 needs Gradle 8.5+, JDK 24 needs 8.14+, JDK 25 needs 9.1+, and Gradle 9 needs JDK 17+ to run.
- A JDK newer than that table is **Not verifiable**, which means the build confirms it. It is never assumed
  compatible.

The Gradle matrix could not be re-fetched from docs.gradle.org in HCG's build environment. The AGP 9.5 row was
checked against developer.android.com.

## Android SDK (Toolchains › Android SDK)

- **Candidates:** the folder saved in HCG, then `ANDROID_HOME`, then `ANDROID_SDK_ROOT`, then Android Studio's
  default location (`~/Library/Android/sdk`, `%LOCALAPPDATA%\Android\Sdk`, `~/Android/Sdk`).
- **Platforms and build-tools are separate components:**
  - a platform counts only when `platforms/android-NN/android.jar` exists;
  - `apksigner`, `zipalign` and `aapt2` are taken from the newest *complete* build-tools version.
- **Licences:** HCG reports whether they are accepted, because Gradle can download a missing platform or build-tools
  only after you accept them with `sdkmanager --licenses`.
- **bundletool** is required only to turn an App Bundle into APKs.

## Project readiness (Mobile Build Studio)

For the selected project, output and signing mode, HCG lists:
- **required** items, which block the build when not ready;
- **optional or not needed** items, which never block.

Rules:
- A missing Flutter SDK never blocks a native Android or Swift build.
- Android tools never block iOS or macOS builds.
- Windows tools never appear as faults on a Mac.
- Simulator builds need no certificate or profile.
- Signed builds need the certificate type the destination needs:
  - Debugging export: a development certificate;
  - App Store Connect, Ad Hoc or Enterprise: an Apple Distribution certificate. A development certificate is never
    treated as distribution readiness.
- Profiles are required for signed device builds only:
  - **manual signing:** the named profile, of the right type, not expired;
  - **automatic signing:** a matching profile, or "Allow provisioning updates".
- iOS on Windows or Linux is explained as **Other host**.

## Builds use the inspected toolchain

Mobile builds start the JDK that passed inspection (its `bin/java`). They get `JAVA_HOME`, `ANDROID_HOME` /
`ANDROID_SDK_ROOT` and PATH entries (JDK bin, platform-tools, the selected build-tools, Flutter bin) from the
inspection report. This works without your shell profile, so a Finder launch builds with the same tools as a Terminal
launch. The build log records which tools were used.

## Checking from the command line

`HCGExecutableMaker --toolchain-report` prints every row with status, path, source and fix. It also prints every JDK
examined, with the reason each one was rejected, and every Android SDK location tried. To see what a Finder launch
sees, run it from an empty environment:

```sh
env -i HOME="$HOME" PATH=/usr/bin:/bin "/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker" --toolchain-report
```
