import Foundation
#if canImport(CryptoKit)
import CryptoKit
#else
import Crypto
#endif

/// The shared core (as StoreManagerKit is for HCG Store Manager): used by both the app and the command-line tool.
public enum ShapeKit {
    public static let name = "Store-shaped app"
    public static func sha256(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}
