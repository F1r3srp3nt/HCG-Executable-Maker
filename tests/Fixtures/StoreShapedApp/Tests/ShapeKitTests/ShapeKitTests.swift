import XCTest
@testable import ShapeKit

final class ShapeKitTests: XCTestCase {
    func testDigest() { XCTAssertEqual(ShapeKit.sha256("").count, 64) }
}
