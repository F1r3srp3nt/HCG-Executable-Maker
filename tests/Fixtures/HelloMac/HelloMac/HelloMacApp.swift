import SwiftUI

@main
struct HelloMacApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
                .frame(minWidth: 360, minHeight: 280)
        }
        Settings {
            SettingsView()
        }
    }
}
