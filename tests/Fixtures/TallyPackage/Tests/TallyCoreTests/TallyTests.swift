import XCTest
@testable import TallyCore

final class TallyTests: XCTestCase {
    func testAddUsesStep() {
        var t = Tally(step: 3)
        t.add(); t.add()
        XCTAssertEqual(t.total, 6)
        XCTAssertEqual(t.history, [3, 6])
    }
}
