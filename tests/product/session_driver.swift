import Foundation
import Darwin

@main
struct SessionDriver {
    static func main() throws {
        let invite = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8)
        let reject = CommandLine.arguments.count > 2 && CommandLine.arguments[2] == "reject"
        let session = DesktopSession(persistPairing: false)
        var connected = false, gotText = false, gotState = false, gotHeartbeat = false
        var finished = false
        func fail(_ message: String) -> Never { fputs(message + "\n", stderr); exit(1) }
        session.onConnection = { value, _ in
            if value { connected = true; if reject { fail("Wrong certificate pin was accepted") } }
        }
        session.onState = { state in
            if wireString(state,"source") == "windows" && wireBool(state,"armed") == false { gotState = true }
        }
        session.onHeartbeat = { gotHeartbeat = true }
        session.onMedia = { message in
            if wireString(message,"type") == "clipboard" && wireString(message,"text") == "双栖 TLS\n🙂" {
                DispatchQueue.main.async { gotText = true }
                session.sendMedia(["type":"test_ack","received":"clipboard"])
            }
        }
        session.onControl = { message in
            if wireString(message,"type") == "test_finish" {
                if !(connected && gotText && gotState && gotHeartbeat) { fail("Missing dual-channel or heartbeat evidence") }
                finished = true; session.stop()
                print("PASS: pinned TLS, two authenticated channels, fragmented UTF-8 frame, state and heartbeat")
                exit(0)
            }
        }
        try session.saveInvitation(invite)
        DispatchQueue.main.asyncAfter(deadline: .now() + (reject ? 6 : 16)) {
            if reject && !connected { print("PASS: wrong certificate pin refused"); session.stop(); exit(0) }
            if !finished { fail("Session test timed out") }
        }
        dispatchMain()
    }
}
