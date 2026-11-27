/// A summary of a series of readings.
public struct FieldSummary: Equatable, Sendable {
    public let readings: [Double]

    public init(readings: [Double]) { self.readings = readings }

    public var count: Int { readings.count }

    public var average: Double { readings.isEmpty ? 0 : readings.reduce(0, +) / Double(readings.count) }
}
