import XCTest

/// Exercises the main screen of the iOS port of HelloMac, which must already be installed in the Simulator.
final class HelloMacPortUITests: XCTestCase {
    override func setUp() { continueAfterFailure = false }

    func testExerciseMainScreen() throws {
        let app = XCUIApplication(bundleIdentifier: "com.hakmicapital.hellomac")
        app.launch()

        let count = app.staticTexts["count"]
        XCTAssertTrue(count.waitForExistence(timeout: 30), "The ported app's main screen did not appear.")
        XCTAssertEqual(count.label, "0")
        snapshot("1-launched")

        let add = app.buttons["add"]
        for _ in 0..<3 { add.tap() }
        XCTAssertEqual(count.label, "3")
        snapshot("2-added-three-times")

        // The step setting moved from the macOS Settings window to the main screen in the port.
        let increment = app.buttons["step-Increment"].exists ? app.buttons["step-Increment"] : app.buttons["Increment"]
        XCTAssertTrue(increment.exists, "Step stepper not found.")
        increment.tap()
        XCTAssertEqual(add.label, "Add 2")
        add.tap()
        XCTAssertEqual(count.label, "5")

        app.buttons["copy"].tap()
        XCTAssertEqual(app.buttons["copy"].label, "Copied")
        snapshot("3-step-two-and-copied")

        app.buttons["reset"].tap()
        XCTAssertEqual(count.label, "0")
        snapshot("4-reset")
    }

    private func snapshot(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
