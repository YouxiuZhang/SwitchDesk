import Foundation

// Compatibility entry point: all artwork now comes from the approved master.
let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
let master = root.appendingPathComponent("design/brand/switchdesk-silver.png")
try Data(contentsOf: master).write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
