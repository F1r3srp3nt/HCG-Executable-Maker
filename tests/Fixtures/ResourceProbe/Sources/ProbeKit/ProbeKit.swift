import Foundation

public enum ProbeKit {
    /// Reads greeting.txt from this library's SwiftPM resource bundle.
    public static func greeting() -> String {
        guard let url = Bundle.module.url(forResource: "greeting", withExtension: "txt"),
              let text = try? String(contentsOf: url, encoding: .utf8) else { return "MISSING library resource" }
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
