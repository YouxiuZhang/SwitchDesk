import Foundation
import CryptoKit

@main
struct FileTransferChecks {
    static func main() throws {
        let root = URL(fileURLWithPath:CommandLine.arguments[1],isDirectory:true)
        let fm=FileManager.default
        try fm.createDirectory(at:root,withIntermediateDirectories:true)
        let originals=root.appendingPathComponent("source/中文目录",isDirectory:true)
        try fm.createDirectory(at:originals.appendingPathComponent("空目录"),withIntermediateDirectories:true)
        let bytes=Data((0..<(1024*1024+7)).map { UInt8($0 % 251) })
        try bytes.write(to:originals.appendingPathComponent("图片.bin"))
        try Data().write(to:originals.appendingPathComponent("empty.txt"))

        func roundTrip(_ corrupted: Bool) throws {
            let sender=FileTransfer(cacheDirectory:root.appendingPathComponent(corrupted ? "sender-bad" : "sender"))
            let receiver=FileTransfer(cacheDirectory:root.appendingPathComponent(corrupted ? "receiver-bad" : "receiver"))
            let done=DispatchSemaphore(value:0)
            var outcome: Result<[URL],Error>?
            sender.send={ message in
                var wire=message
                if corrupted && wireString(message,"type")=="file_end" { wire["sha256"]=String(repeating:"0",count:64) }
                receiver.receive(wire)
            }
            receiver.send={ message in sender.receive(message) }
            sender.setAvailable(connected:true,enabled:true)
            receiver.setAvailable(connected:true,enabled:true)
            sender.makeOffer(urls:[originals]) { result in
                do {
                    let offer=try result.get()
                    let roots=offer["roots"] as! [String]
                    let entries=try FileTransfer.parse(entries:offer["entries"] as! [WireMessage],roots:roots)
                    receiver.fetchRemote(id:wireString(offer,"id"),entries:entries,roots:roots) { result in outcome=result;done.signal() }
                } catch { outcome = .failure(error);done.signal() }
            }
            precondition(done.wait(timeout:.now()+15) == .success,"transfer deadlock/timeout")
            guard let outcome else { fatalError("no transfer result") }
            if corrupted {
                if case .success = outcome { fatalError("corrupt SHA256 accepted") }
                print("PASS: corrupted SHA256 rejected before publishing files")
            } else {
                let urls=try outcome.get()
                precondition(urls.count==1)
                let received = try Data(contentsOf:urls[0].appendingPathComponent("图片.bin"))
                let empty = try Data(contentsOf:urls[0].appendingPathComponent("empty.txt"))
                precondition(received == bytes)
                precondition(empty.isEmpty)
                var directory:ObjCBool=false
                precondition(fm.fileExists(atPath:urls[0].appendingPathComponent("空目录").path,isDirectory:&directory) && directory.boolValue)
                print("PASS: streamed 1 MiB+7 bytes, Chinese folder, empty file/directory, SHA256 and atomic publish")
            }
            sender.setAvailable(connected:false,enabled:false);receiver.setAvailable(connected:false,enabled:false)
        }
        try roundTrip(false)
        try roundTrip(true)

        let cancel=FileTransfer(cacheDirectory:root.appendingPathComponent("cancel"))
        cancel.setAvailable(connected:true,enabled:true)
        let done=DispatchSemaphore(value:0)
        var rejected=false
        cancel.send={ message in if wireString(message,"type")=="file_request" { cancel.cancelAll() } }
        cancel.fetchRemote(id:UUID().uuidString,entries:[FileEntry(path:"test.txt",directory:false,size:1)],roots:["test.txt"]) { result in
            if case .failure = result { rejected=true };done.signal()
        }
        precondition(done.wait(timeout:.now()+5) == .success && rejected,"cancel did not release waiter")
        print("PASS: cancel releases pending file paste")

        let symlink=originals.appendingPathComponent("symlink")
        try fm.createSymbolicLink(at:symlink,withDestinationURL:originals.appendingPathComponent("图片.bin"))
        let source=FileTransfer(cacheDirectory:root.appendingPathComponent("link"))
        source.setAvailable(connected:true,enabled:true)
        let linkDone=DispatchSemaphore(value:0)
        var linkRejected=false
        source.makeOffer(urls:[symlink]) { result in if case .failure = result { linkRejected=true };linkDone.signal() }
        precondition(linkDone.wait(timeout:.now()+5) == .success && linkRejected,"symlink source accepted")
        print("PASS: symlink source rejected")
    }
}
