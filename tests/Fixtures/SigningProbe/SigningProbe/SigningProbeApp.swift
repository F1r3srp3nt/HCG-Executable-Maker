import SwiftUI

/// HCG Executable Maker's signing fixture (shaped like Unimatch): a Development Run that exists only in Debug builds.
@main
struct SigningProbeApp: App {
    var body: some Scene {
        WindowGroup {
            VStack(spacing: 12) {
                Text("Built by HCG Executable Maker")
                #if PROBE_DEVELOPMENT_RUN
                Text(DevelopmentRun.banner)
                #endif
            }
        }
    }
}

#if PROBE_DEVELOPMENT_RUN
/// Present only in Debug builds (SWIFT_ACTIVE_COMPILATION_CONDITIONS[config=Debug]). CI checks that Release builds
/// and archives contain none of it.
enum DevelopmentRun {
    static let banner = "SIGNINGPROBE-DEVELOPMENT-RUN: simulated data, Debug only"
}
#endif
