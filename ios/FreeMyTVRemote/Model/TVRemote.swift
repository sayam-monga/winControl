import Foundation
import Network
import Observation
import UIKit

/// An app on the TV the phone can open.
struct TVApp: Identifiable, Hashable {
    let pkg: String
    let name: String
    let icon: UIImage?
    var id: String { pkg }
}

/// The connection to one TV: pairing, button presses, typing, apps. Talks to RemoteServer on the TV: TCP, one JSON
/// object per line.
@MainActor @Observable
final class TVRemote {
    enum State: Equatable {
        case idle
        case connecting
        /// The TV is showing a code; the person types it here.
        case enterCode
        case connected
        case failed(String)
    }

    private(set) var state: State = .idle
    private(set) var target: RemoteTarget?
    private(set) var tvName = ""
    private(set) var apps: [TVApp] = []
    private(set) var loadingApps = false
    private(set) var windowsDevices: [WindowsRemoteDevice] = []
    private(set) var windowsDiscoveryMessage: String?
    private(set) var fallbackPairingCode: String?
    /// A wrong code was typed; the field shakes and clears.
    private(set) var codeRejected = 0

    var isWindowsPC: Bool {
        guard let target else { return false }
        switch target {
        case .windowsPC, .windowsPCPeripheral: return true
        case .tv: return false
        }
    }

    var isWindowsPCFallback: Bool {
        guard let target else { return false }
        if case .windowsPCPeripheral = target { return true }
        return false
    }

    var displayName: String {
        guard let target else { return "" }
        if case .tv = target, !tvName.isEmpty { return tvName }
        return target.label
    }

    var pairingCodeLength: Int { isWindowsPC ? 6 : 4 }

    private var connection: NWConnection?
    private let windowsTransport: WindowsRemoteTransport
    private var buffer = Data()
    private var tvID = ""
    private var pingTimer: Timer?
    private var retryTask: Task<Void, Never>?
    /// WHY: the server drops a link that's quiet for 30 s; a ping every 5 s keeps it open and spots a dead one quickly.
    private static let pingInterval: TimeInterval = 5

    private static let lastTargetKey = "lastTarget"

    init() {
        windowsTransport = WindowsRemoteTransport()
        windowsTransport.onDevicesChanged = { [weak self] devices in
            Task { @MainActor in self?.windowsDevices = devices }
        }
        windowsTransport.onDiscoveryMessageChanged = { [weak self] message in
            Task { @MainActor in self?.windowsDiscoveryMessage = message }
        }
        windowsTransport.onStateChanged = { [weak self] state in
            Task { @MainActor in self?.windowsLinkChanged(state) }
        }
        windowsTransport.onPairingCodeRejected = { [weak self] in
            Task { @MainActor in self?.codeRejected += 1 }
        }
        windowsTransport.onFallbackPairingCodeChanged = { [weak self] code in
            Task { @MainActor in self?.fallbackPairingCode = code }
        }
        windowsTransport.onFallbackConnection = { [weak self] identifier, name, paired in
            Task { @MainActor in
                guard let self else { return }
                self.target = .windowsPCPeripheral(identifier: identifier, name: name)
                if let data = try? JSONEncoder().encode(self.target) { UserDefaults.standard.set(data, forKey: Self.lastTargetKey) }
                self.tvName = name
                self.apps = []
                self.state = paired ? .connected : .enterCode
            }
        }
        windowsTransport.onFallbackDisconnected = { [weak self] message in
            Task { @MainActor in
                guard let self, self.isWindowsPCFallback else { return }
                self.state = .failed(message)
            }
        }
        if let data = UserDefaults.standard.data(forKey: Self.lastTargetKey),
           let saved = try? JSONDecoder().decode(RemoteTarget.self, from: data) {
            target = saved
        } else if let data = UserDefaults.standard.data(forKey: Self.lastTargetKey),
                  let saved = try? JSONDecoder().decode(TVTarget.self, from: data) {
            // Existing installs saved TVTarget directly; accept that format and migrate on the next connection.
            target = .tv(saved)
        }
    }

    // MARK: - Connecting

    func connect(to newTarget: TVTarget) {
        windowsTransport.disconnect()
        target = .tv(newTarget)
        if let data = try? JSONEncoder().encode(target) { UserDefaults.standard.set(data, forKey: Self.lastTargetKey) }
        tvName = newTarget.label
        apps = []
        reconnect()
    }

    func connect(toWindowsPC device: WindowsRemoteDevice) {
        teardown()
        target = .windowsPC(identifier: device.id, name: device.name)
        if let data = try? JSONEncoder().encode(target) { UserDefaults.standard.set(data, forKey: Self.lastTargetKey) }
        tvName = device.name
        apps = []
        state = .connecting
        windowsTransport.connect(to: device)
    }

    func startWindowsDiscovery() {
        windowsTransport.startDiscovery()
        windowsTransport.startFallbackAdvertising()
    }

    func stopWindowsDiscovery() {
        windowsTransport.stopDiscovery()
        windowsTransport.stopFallbackAdvertising()
    }

    /// Connects (again) to the last selected TV or Windows PC.
    func reconnect() {
        guard let target else { return }
        teardown()
        state = .connecting
        switch target {
        case .windowsPC(let identifier, let name):
            tvName = name
            windowsTransport.reconnect(identifier: identifier, name: name)
            return
        case .windowsPCPeripheral(let identifier, let name):
            tvName = name
            windowsTransport.reconnectAsPeripheral(identifier: identifier, name: name)
            return
        case .tv(let tvTarget):
            tvName = tvTarget.label
            reconnect(to: tvTarget)
        }
    }

    private func reconnect(to target: TVTarget) {
        let params = NWParameters.tcp
        if let tcp = params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
            tcp.noDelay = true   // button presses are tiny: send each one at once
            tcp.connectionTimeout = 6
        }
        let c = NWConnection(to: target.endpoint, using: params)
        connection = c
        c.stateUpdateHandler = { [weak self] s in
            Task { @MainActor in self?.connectionChanged(s, c) }
        }
        c.start(queue: .main)
        receive(on: c)
    }

    func disconnect() {
        teardown()
        windowsTransport.disconnect()
        state = .idle
    }

    private func teardown() {
        retryTask?.cancel()
        retryTask = nil
        pingTimer?.invalidate()
        pingTimer = nil
        connection?.cancel()
        connection = nil
        buffer.removeAll()
    }

    private func connectionChanged(_ s: NWConnection.State, _ c: NWConnection) {
        guard c === connection, !isWindowsPC else { return }
        switch s {
        case .ready:
            pingTimer = Timer.scheduledTimer(withTimeInterval: Self.pingInterval, repeats: true) { [weak self] _ in
                Task { @MainActor in self?.send(["t": "ping"]) }
            }
        case .failed(let error), .waiting(let error):
            fail(Self.describe(error))
        default:
            break
        }
    }

    private func fail(_ message: String) {
        let wasConnected = state == .connected
        teardown()
        state = .failed(message)
        // A link that was working dropped (Wi-Fi blip, TV asleep): try again quietly a few seconds later.
        if wasConnected {
            retryTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(3))
                guard !Task.isCancelled else { return }
                self?.reconnect()
            }
        }
    }

    private static func describe(_ error: NWError) -> String {
        switch error {
        case .posix(let code) where code == .ECONNREFUSED:
            return "The TV answered, but its remote isn't running. Open Free My TV's home screen on the TV once."
        case .posix(let code) where code == .ETIMEDOUT || code == .EHOSTUNREACH || code == .ENETUNREACH:
            return "Couldn't reach the TV. Check that it's on and on the same Wi-Fi as this phone."
        case .dns, .posix:
            return "Couldn't reach the TV. Check that it's on and on the same Wi-Fi as this phone."
        default:
            return "Lost the connection to the TV."
        }
    }

    // MARK: - Messages

    private func receive(on c: NWConnection) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, done, error in
            Task { @MainActor in
                guard let self, c === self.connection else { return }
                if let data { self.buffer.append(data); self.drainLines() }
                if let error { self.fail(Self.describe(error)); return }
                if done { self.fail("The TV closed the connection."); return }
                self.receive(on: c)
            }
        }
    }

    private func drainLines() {
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = buffer[buffer.startIndex..<newline]
            buffer.removeSubrange(buffer.startIndex...newline)
            guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
            handle(obj)
        }
    }

    private func handle(_ m: [String: Any]) {
        guard !isWindowsPC else { return }
        switch m["t"] as? String {
        case "hello":
            tvID = m["id"] as? String ?? ""
            if let name = m["name"] as? String, !name.isEmpty { tvName = name }
            if let token = TokenStore.token(for: tvID) {
                send(["t": "auth", "token": token])
            } else {
                startPairing()
            }
        case "auth":
            if m["ok"] as? Bool == true {
                state = .connected
                requestApps()
            } else {
                // The TV forgot this phone (or was reset): pair again.
                TokenStore.forget(tvID)
                startPairing()
            }
        case "pairing":
            state = .enterCode
            #if DEBUG
            // Testing only: `-autoCode 1234` as a launch argument types the code (the simulator can't be tapped from scripts).
            if let code = UserDefaults.standard.string(forKey: "autoCode"), code.count == 4 { submit(code: code) }
            #endif
        case "paired":
            if let token = m["token"] as? String { TokenStore.save(token, for: tvID) }
            state = .connected
            requestApps()
        case "apps":
            loadingApps = false
            let list = m["list"] as? [[String: Any]] ?? []
            apps = list.compactMap { a in
                guard let pkg = a["pkg"] as? String, let name = a["name"] as? String else { return nil }
                let icon = (a["icon"] as? String).flatMap { Data(base64Encoded: $0) }.flatMap { UIImage(data: $0) }
                return TVApp(pkg: pkg, name: name, icon: icon)
            }
        case "error":
            let msg = m["msg"] as? String ?? ""
            if msg == "locked" {
                // Too many wrong codes: the TV refuses new pairings for a while.
                let wait = m["wait"] as? Int ?? 30
                let time = wait >= 120 ? "\(wait / 60) minutes" : "\(wait) seconds"
                state = .failed("Too many wrong codes. Try again in \(time).")
                return
            }
            if state == .enterCode {
                if msg == "wrong code" {
                    codeRejected += 1
                } else {
                    // Too many wrong codes, or it timed out: a new code on the TV.
                    codeRejected += 1
                    startPairing()
                }
            }
        default:
            break
        }
    }

    private func send(_ m: [String: Any]) {
        guard let connection, var data = try? JSONSerialization.data(withJSONObject: m) else { return }
        data.append(0x0A)
        connection.send(content: data, completion: .contentProcessed { _ in })
    }

    // MARK: - Pairing

    private func startPairing() {
        send(["t": "pair", "name": UIDevice.current.name])
    }

    func submit(code: String) {
        if isWindowsPC {
            windowsTransport.submitPairingCode(code)
            return
        }
        send(["t": "code", "code": code])
    }

    func forgetFallbackPairing() {
        guard isWindowsPCFallback else { return }
        windowsTransport.forgetFallbackPairing()
    }

    func stopWindowsFallback() {
        guard isWindowsPCFallback else { return }
        windowsTransport.stopFallbackConnection()
    }

    /// Asks the TV for a fresh code (the old one expired or the person closed it).
    func newCode() {
        guard !isWindowsPC else { return }
        startPairing()
    }

    // MARK: - Remote

    func press(_ key: Key) {
        guard state == .connected else { return }
        if isWindowsPC {
            windowsTransport.press(key)
            return
        }
        send(["t": "key", "k": key.rawValue])
    }

    func longPress(_ key: Key) {
        guard state == .connected else { return }
        if isWindowsPC {
            if key == .center { windowsTransport.click(right: true) }
            return
        }
        send(["t": "key", "k": key.rawValue, "a": "long"])
    }

    func type(_ text: String) {
        guard state == .connected, !text.isEmpty else { return }
        if isWindowsPC {
            windowsTransport.type(text)
            return
        }
        send(["t": "text", "s": text])
    }

    func click(right: Bool = false) {
        guard state == .connected else { return }
        if isWindowsPC {
            windowsTransport.click(right: right)
        } else if right {
            longPress(.center)
        } else {
            press(.center)
        }
    }

    func movePointer(deltaX: CGFloat, deltaY: CGFloat) {
        guard state == .connected, isWindowsPC else { return }
        windowsTransport.movePointer(deltaX: deltaX, deltaY: deltaY)
    }

    func requestApps() {
        guard state == .connected else { return }
        loadingApps = true
        send(["t": "apps"])
    }

    func launch(_ app: TVApp) {
        guard state == .connected, !isWindowsPC else { return }
        send(["t": "launch", "pkg": app.pkg])
    }

    private func windowsLinkChanged(_ newState: WindowsLinkState) {
        guard isWindowsPC else { return }
        switch newState {
        case .idle: state = .idle
        case .connecting: state = .connecting
        case .waitingForPairing: state = .enterCode
        case .connected: state = .connected
        case .failed(let message): state = .failed(message)
        }
    }
}
