# Building Android apps

## Requirements (detected on the Toolchains page; never installed automatically)

| Tool | Why | How HCG finds it |
|---|---|---|
| JDK 17+ (with `javac`) | Gradle / AGP 8 | `JAVA_HOME`, `PATH`, Android Studio's bundled JBR on macOS |
| Android SDK (platforms, build-tools) | compilation, `apksigner`, `zipalign`, `aapt2` | `ANDROID_HOME`, `ANDROID_SDK_ROOT`, default SDK locations |
| The project's **Gradle wrapper** | the build itself | `gradle/wrapper/gradle-wrapper.jar` + `gradle-wrapper.properties` in the project |
| bundletool (optional) | AAB → APK | `BUNDLETOOL_JAR`, or `bundletool*.jar` in HCG's `tools/` folder |

## Detection (static)

`settings.gradle[.kts]` → included modules; each module's `build.gradle[.kts]` → `com.android.application` plugin
(including version-catalog aliases, ignoring `apply false`), `namespace`, `applicationId`, `minSdk`/`targetSdk`/
`compileSdk` (incl. `compileSdk { version = release(36) }`), `versionCode`/`versionName`, build types, product flavors
and whether a `signingConfig` is defined. AGP/Gradle/JDK compatibility is checked against a table (AGP 7.0–8.13; newer
versions are reported as *not in HCG's table* rather than guessed).

## How the build runs

```
java -Xmx64m -Dorg.gradle.appname=gradlew -classpath <project>/gradle/wrapper/gradle-wrapper.jar \
     org.gradle.wrapper.GradleWrapperMain --no-daemon --console=plain --no-configuration-cache :app:assembleDebug
```

This is exactly what `gradlew` does, without a shell. Every value is a separate argument; project data is never
interpolated into a command string. Environment:

- secret-like variables removed (`*PASSWORD*`, `*TOKEN*`, `*SIGNING*`, `GITHUB_*`, …);
- `JAVA_HOME`, `ANDROID_HOME`/`ANDROID_SDK_ROOT` set from detection;
- `GRADLE_USER_HOME` and `ANDROID_USER_HOME` point to **HCG-managed folders**, so `~/.gradle/gradle.properties`
  (which often holds signing passwords and repository credentials) and `~/.android` keystores are never read;
- proxy host/port (never credentials) from `HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY` are translated into Java system
  properties, because Java ignores those variables.

Before building, the log shows the wrapper's `distributionUrl` and the wrapper jar's SHA-256; a non-HTTPS or
non-`services.gradle.org` distribution, or a missing `distributionSha256Sum`, is reported as a warning.

Outputs are taken only from `<module>/build/outputs/apk/…` or `…/bundle/<variant>/` **and only if written during
this build** — a stale APK shipped inside an imported project is never reported as a result.

## Signing

- **Debug APK** — signed by AGP with the debug key in HCG's Android user home.
- **Release APK / AAB, project signing** — whatever the project's `signingConfig` does. Because credentials from the
  environment and personal Gradle properties are not passed, a project that expects them fails with Gradle's own error.
- **Unsigned** — offered when the module defines no release signing; refused otherwise (HCG does not edit project
  files to remove it). Unsigned APKs cannot be installed.
- **HCG local debug key** — Android Studio's injected-signing properties with HCG's debug keystore
  (`<data>/signing/hcg-debug.keystore`, alias `androiddebugkey`, the public password `android`, masked in logs anyway).
  For testing only.

Production upload keys are never generated, stored or requested by HCG.

## AAB → APK (bundletool)

`java -jar bundletool.jar build-apks --bundle=… --output=….apks --overwrite --mode=universal` (or
`--device-spec=spec.json`), always with explicit `--ks` pointing at HCG's debug keystore so bundletool never falls back
to `~/.android/debug.keystore` silently. For universal mode HCG extracts `universal.apk` (bounded read) next to the
`.apks`. The result is a **test** APK, not a Play-signed release. The opposite direction (APK → AAB) is not possible
and is not offered.
