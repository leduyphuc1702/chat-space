import Foundation

enum BridgeError: LocalizedError {
    case launch(String)
    case remote(code: String, message: String)
    case exited

    var errorDescription: String? {
        switch self {
        case .launch(let m): m
        case .remote(_, let m): m
        case .exited: "Bridge process exited"
        }
    }
}

/// One sidecar process speaking newline-delimited JSON (docs/PROTOCOL.md).
final class BridgeProcess: @unchecked Sendable {
    let platform: Platform

    private let lock = NSLock()
    private var process: Process?
    private var stdin: FileHandle?
    private var buffer = Data()
    private var nextId = 1
    private var pending: [String: CheckedContinuation<JSONValue, Error>] = [:]
    private var logHandle: FileHandle?

    var onEvent: (@MainActor @Sendable (String, JSONValue) -> Void)?
    var onExit: (@MainActor @Sendable (Int32) -> Void)?

    init(platform: Platform) {
        self.platform = platform
    }

    var isRunning: Bool { lock.withLock { process?.isRunning ?? false } }

    func start() throws {
        let (exe, args) = try platform.bridgeCommand()
        let p = Process()
        p.executableURL = exe
        p.arguments = args
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = (env["PATH"] ?? "/usr/bin:/bin") + ":/opt/homebrew/bin:/usr/local/bin"
        p.environment = env

        let inPipe = Pipe(), outPipe = Pipe(), errPipe = Pipe()
        p.standardInput = inPipe
        p.standardOutput = outPipe
        p.standardError = errPipe

        let logURL = platform.dataDirectory.appendingPathComponent("bridge.log")
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
        }
        let log = try? FileHandle(forWritingTo: logURL)
        log?.truncateFile(atOffset: 0)
        logHandle = log

        outPipe.fileHandleForReading.readabilityHandler = { [weak self] h in
            let data = h.availableData
            if data.isEmpty { h.readabilityHandler = nil; return }
            self?.consume(data)
        }
        errPipe.fileHandleForReading.readabilityHandler = { [weak self] h in
            let data = h.availableData
            if data.isEmpty { h.readabilityHandler = nil; return }
            self?.logHandle?.write(data)
        }
        p.terminationHandler = { [weak self] proc in
            guard let self else { return }
            let waiting = self.lock.withLock { () -> [CheckedContinuation<JSONValue, Error>] in
                let w = Array(self.pending.values)
                self.pending.removeAll()
                return w
            }
            waiting.forEach { $0.resume(throwing: BridgeError.exited) }
            let status = proc.terminationStatus
            let cb = self.onExit
            Task { @MainActor in cb?(status) }
        }

        try p.run()
        lock.withLock {
            process = p
            stdin = inPipe.fileHandleForWriting
        }
    }

    func stop() {
        lock.withLock {
            try? stdin?.close()
            stdin = nil
        }
        let p = lock.withLock { process }
        DispatchQueue.global().asyncAfter(deadline: .now() + 2) {
            if p?.isRunning == true { p?.terminate() }
        }
    }

    func call(_ method: String, _ params: [String: JSONValue] = [:]) async throws -> JSONValue {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<JSONValue, Error>) in
            let (id, handle) = lock.withLock { () -> (String, FileHandle?) in
                let id = String(nextId)
                nextId += 1
                pending[id] = cont
                return (id, stdin)
            }
            guard let handle else {
                _ = lock.withLock { pending.removeValue(forKey: id) }
                cont.resume(throwing: BridgeError.exited)
                return
            }
            let frame: JSONValue = .object(["id": .string(id), "method": .string(method), "params": .object(params)])
            do {
                var data = try JSONEncoder().encode(frame)
                data.append(0x0A)
                try handle.write(contentsOf: data)
            } catch {
                _ = lock.withLock { pending.removeValue(forKey: id) }
                cont.resume(throwing: error)
            }
        }
    }

    private func consume(_ data: Data) {
        var lines: [Data] = []
        lock.withLock {
            buffer.append(data)
            while let nl = buffer.firstIndex(of: 0x0A) {
                lines.append(buffer.subdata(in: buffer.startIndex..<nl))
                buffer.removeSubrange(buffer.startIndex...nl)
            }
        }
        for line in lines where !line.isEmpty {
            handle(line)
        }
    }

    private func handle(_ line: Data) {
        guard let frame = try? JSONDecoder().decode(JSONValue.self, from: line) else {
            logHandle?.write("[app] non-JSON stdout: ".data(using: .utf8)! + line + Data([0x0A]))
            return
        }
        if let event = frame["event"]?.stringValue {
            let data = frame["data"] ?? .null
            let cb = onEvent
            Task { @MainActor in cb?(event, data) }
            return
        }
        guard let id = frame["id"]?.stringValue else { return }
        let cont = lock.withLock { pending.removeValue(forKey: id) }
        if let err = frame["error"], err != .null {
            cont?.resume(throwing: BridgeError.remote(
                code: err["code"]?.stringValue ?? "internal",
                message: err["message"]?.stringValue ?? "Unknown error"))
        } else {
            cont?.resume(returning: frame["result"] ?? .null)
        }
    }
}
