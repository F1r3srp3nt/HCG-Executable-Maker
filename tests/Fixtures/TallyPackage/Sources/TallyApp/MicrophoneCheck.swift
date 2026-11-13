import AVFoundation
import SwiftUI

/// Reports microphone permission (the app would record a voice note in a later version).
@MainActor
final class MicrophoneCheck: ObservableObject {
    @Published var status = "Microphone: checking…"

    func refresh() async {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: status = "Microphone: allowed"
        case .denied, .restricted: status = "Microphone: not allowed"
        case .notDetermined: status = "Microphone: not asked yet"
        @unknown default: status = "Microphone: unknown"
        }
    }
}
