import Foundation

/// A counter with a configurable step and a history of totals.
public struct Tally: Equatable, Sendable {
    public private(set) var total: Int = 0
    public private(set) var history: [Int] = []
    public var step: Int

    public init(step: Int = 1) { self.step = max(1, step) }

    public mutating func add() {
        total += step
        history.append(total)
    }

    public mutating func reset() {
        total = 0
        history.removeAll()
    }

    public var summary: String {
        history.isEmpty ? "No taps yet" : "\(history.count) tap(s), last total \(total)"
    }
}
