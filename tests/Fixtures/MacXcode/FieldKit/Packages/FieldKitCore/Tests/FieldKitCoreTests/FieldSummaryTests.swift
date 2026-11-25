import XCTest
@testable import FieldKitCore

final class FieldSummaryTests: XCTestCase {
    func testAverage() {
        XCTAssertEqual(FieldSummary(readings: [1, 2, 3]).average, 2, accuracy: 0.0001)
    }

    func testEmpty() {
        XCTAssertEqual(FieldSummary(readings: []).average, 0)
        XCTAssertEqual(FieldSummary(readings: []).count, 0)
    }
}
