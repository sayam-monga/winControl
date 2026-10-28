import CoreBluetooth
import CryptoKit
import Foundation

struct WindowsRemoteDevice: Identifiable {
    let id: UUID
    let name: String
    fileprivate let peripheral: CBPeripheral
}

enum WindowsLinkState: Equatable {
    case idle
    case connecting
    case waitingForPairing
    case connected
    case failed(String)
}

private enum WindowsOpcode: UInt8 {
    case arrow = 0x01
    case pointerMove = 0x02
    case leftClick = 0x03
    case rightClick = 0x04
    case keyPress = 0x10
    case text = 0x11
    case mediaKey = 0x20
}

private enum WindowsKey: UInt8 {
    case enter = 0x01
    case escape = 0x02
    case backspace = 0x03
    case home = 0x04
    case contextMenu = 0x05
}

private enum WindowsMediaKey: UInt8 {
    case playPause = 0x01
    case volumeUp = 0x02
    case volumeDown = 0x03
    case mute = 0x04
}

private enum WindowsArrow: UInt8 {
    case up = 0x00
    case down = 0x01
    case left = 0x02
    case right = 0x03
}

private enum WindowsPairingStatus: UInt8 {
    case idle = 0
    case invalidCode = 1
    case locked = 2
    case paired = 3
    case codeExpired = 4
    case storageError = 5
}

/// Finds and controls the Windows receiver acting as a BLE GATT server.
final class WindowsRemoteTransport: NSObject {
    private static let serviceUUID = CBUUID(string: "6D4F0001-4A2B-4B91-A6D7-31B7C2E10510")
    private static let commandUUID = CBUUID(string: "6D4F0002-4A2B-4B91-A6D7-31B7C2E10510")
    private static let pairingUUID = CBUUID(string: "6D4F0003-4A2B-4B91-A6D7-31B7C2E10510")
    private static let statusUUID = CBUUID(string: "6D4F0004-4A2B-4B91-A6D7-31B7C2E10510")
    private static let protocolVersion: UInt8 = 1 // Matches the first version of the Windows GATT frame contract.
    private static let pairingOpcode: UInt8 = 0x01 // Pairing writes use a fixed opcode separate from input commands.
    private static let maximumTextChunk = 6 // Six UTF-8 bytes is the Windows protocol payload limit per command.
    private static let authenticationTagLength = 8 // The Windows receiver compares the first eight HMAC-SHA-256 bytes.
    private static let pairingCodeLength = 6 // The Windows receiver displays six ASCII digits.
    private static let maximumPointerDelta: Int16 = 2048 // This matches the receiver's per-frame movement bound.
    private static let frameHeaderLength = 6 // Version, opcode, and sequence occupy six bytes before the payload.
    private static let pointerPayloadLength = 4 // Pointer movement is one signed 16-bit x delta and one y delta.

    var onDevicesChanged: (([WindowsRemoteDevice]) -> Void)?
    var onStateChanged: ((WindowsLinkState) -> Void)?
    var onDiscoveryMessageChanged: ((String?) -> Void)?
    var onPairingCodeRejected: (() -> Void)?

    private(set) var devices: [WindowsRemoteDevice] = []
    private(set) var state: WindowsLinkState = .idle
    private(set) var discoveryMessage: String?

    private var central: CBCentralManager!
    private var discoveryRequested = false
    private var discovered: [UUID: WindowsRemoteDevice] = [:]
    private var targetIdentifier: UUID?
    private var targetName = "Windows PC"
    private var tokenAccount = ""
    private var token: Data?
    private var activePeripheral: CBPeripheral?
    private var commandCharacteristic: CBCharacteristic?
    private var pairingCharacteristic: CBCharacteristic?
    private var statusCharacteristic: CBCharacteristic?
    private var pairingAttemptPending = false
    private var pairingWritePending = false
    private var pairingReadPending = false
    private var pairingTokenAwaitingStatus = false
    private var sequence: UInt32 = 0
    private var commandQueue: [Data] = []
    private var inFlightCommand: Data?
    private var pointerRemainderX = 0.0
    private var pointerRemainderY = 0.0

    override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: .main)
    }

    func startDiscovery() {
        discoveryRequested = true
        guard central.state == .poweredOn else {
            updateBluetoothMessage(for: central.state)
            return
        }
        scanForWindowsReceivers()
    }

    func stopDiscovery() {
        discoveryRequested = false
        if central.isScanning { central.stopScan() }
        if case .connecting = state, activePeripheral == nil, targetIdentifier == nil {
            setState(.idle)
        }
    }

    func connect(to device: WindowsRemoteDevice) {
        disconnect()
        targetIdentifier = device.id
        targetName = device.name
        tokenAccount = Self.keychainAccount(for: device.id)
        token = TokenStore.data(for: tokenAccount)
        beginConnect(to: device.peripheral)
    }

    func reconnect(identifier: UUID, name: String) {
        disconnect()
        targetIdentifier = identifier
        targetName = name
        tokenAccount = Self.keychainAccount(for: identifier)
        token = TokenStore.data(for: tokenAccount)
        setState(.connecting)
        guard central.state == .poweredOn else {
            updateBluetoothMessage(for: central.state)
            return
        }

        if let peripheral = central.retrievePeripherals(withIdentifiers: [identifier]).first {
            beginConnect(to: peripheral)
        } else {
            // CoreBluetooth may need to see the receiver again before it can reconnect to its saved identifier.
            discoveryRequested = true
            scanForWindowsReceivers()
            setDiscoveryMessage("Looking for \(name) over Bluetooth. Keep the Windows receiver open nearby.")
        }
    }

    func disconnect() {
        discoveryRequested = false
        if central.isScanning { central.stopScan() }
        if let peripheral = activePeripheral {
            activePeripheral = nil
            central.cancelPeripheralConnection(peripheral)
        }
        targetIdentifier = nil
        tokenAccount = ""
        token = nil
        clearCharacteristics()
        commandQueue.removeAll()
        inFlightCommand = nil
        pairingAttemptPending = false
        pairingWritePending = false
        pairingReadPending = false
        setState(.idle)
    }

    func submitPairingCode(_ code: String) {
        guard state == .waitingForPairing,
              code.utf8.count == Self.pairingCodeLength,
              code.utf8.allSatisfy({ (48...57).contains($0) }),
              let peripheral = activePeripheral,
              let characteristic = pairingCharacteristic else { return }

        var request = Data([Self.protocolVersion, Self.pairingOpcode])
        request.append(contentsOf: code.utf8)
        pairingAttemptPending = true
        pairingWritePending = true
        peripheral.writeValue(request, for: characteristic, type: .withResponse)
    }

    func press(_ key: Key) {
        switch key {
        case .up: sendArrow(.up)
        case .down: sendArrow(.down)
        case .left: sendArrow(.left)
        case .right: sendArrow(.right)
        case .center, .enter: sendKey(.enter) // The arrow pad's center is keyboard Enter.
        case .back: sendKey(.escape)
        case .delete: sendKey(.backspace)
        case .home: sendKey(.home)
        case .menu: sendKey(.contextMenu)
        case .playPause: sendMediaKey(.playPause)
        case .volumeUp: sendMediaKey(.volumeUp)
        case .volumeDown: sendMediaKey(.volumeDown)
        case .mute: sendMediaKey(.mute)
        case .power, .rewind, .fastForward, .settings, .input:
            break
        }
    }

    func click(right: Bool) {
        enqueue(opcode: right ? .rightClick : .leftClick, payload: Data())
    }

    func movePointer(deltaX: CGFloat, deltaY: CGFloat) {
        guard deltaX.isFinite, deltaY.isFinite else { return }
        let limit = Double(Self.maximumPointerDelta)
        let totalX = max(-limit, min(limit, Double(deltaX) + pointerRemainderX))
        let totalY = max(-limit, min(limit, Double(deltaY) + pointerRemainderY))
        let dx = Int16(totalX.rounded(.towardZero))
        let dy = Int16(totalY.rounded(.towardZero))
        pointerRemainderX = totalX - Double(dx)
        pointerRemainderY = totalY - Double(dy)
        guard dx != 0 || dy != 0 else { return }

        var payload = Data()
        var littleX = dx.littleEndian
        var littleY = dy.littleEndian
        withUnsafeBytes(of: &littleX) { payload.append(contentsOf: $0) }
        withUnsafeBytes(of: &littleY) { payload.append(contentsOf: $0) }
        enqueuePointer(payload)
    }

    func type(_ text: String) {
        var chunk = Data()
        for scalar in text.unicodeScalars {
            let scalarBytes = Data(String(scalar).utf8)
            if chunk.count + scalarBytes.count > Self.maximumTextChunk {
                enqueue(opcode: .text, payload: chunk)
                chunk.removeAll(keepingCapacity: true)
            }
            chunk.append(scalarBytes)
        }
        if !chunk.isEmpty { enqueue(opcode: .text, payload: chunk) }
    }

    private static func keychainAccount(for identifier: UUID) -> String {
        "windows-\(identifier.uuidString.lowercased())"
    }

    private func beginConnect(to peripheral: CBPeripheral) {
        discoveryRequested = false
        if central.isScanning { central.stopScan() }
        clearCharacteristics()
        activePeripheral = peripheral
        peripheral.delegate = self
        setState(.connecting)
        central.connect(peripheral, options: nil)
    }

    private func scanForWindowsReceivers() {
        guard central.state == .poweredOn else { return }
        if !central.isScanning {
            central.scanForPeripherals(
                withServices: [Self.serviceUUID],
                options: [CBCentralManagerScanOptionAllowDuplicatesKey: false]
            )
        }
        if targetIdentifier == nil {
            setDiscoveryMessage("Scanning for Windows PCs over Bluetooth. Keep the Windows receiver open nearby.")
        }
    }

    private func setState(_ newState: WindowsLinkState) {
        guard state != newState else { return }
        state = newState
        onStateChanged?(newState)
    }

    private func setDiscoveryMessage(_ message: String?) {
        guard discoveryMessage != message else { return }
        discoveryMessage = message
        onDiscoveryMessageChanged?(message)
    }

    private func updateBluetoothMessage(for state: CBManagerState) {
        switch state {
        case .poweredOff:
            setDiscoveryMessage("Turn on Bluetooth on the iPhone to find Windows PCs.")
        case .unauthorized:
            setDiscoveryMessage("Allow Bluetooth access for Free My TV in iPhone Settings.")
        case .unsupported:
            setDiscoveryMessage("Bluetooth Low Energy is not available on this iPhone.")
        case .resetting, .unknown:
            setDiscoveryMessage("Bluetooth is starting. Keep this screen open.")
        case .poweredOn:
            setDiscoveryMessage(nil)
        @unknown default:
            setDiscoveryMessage("Bluetooth is unavailable on this iPhone.")
        }
    }

    private func clearCharacteristics() {
        commandCharacteristic = nil
        pairingCharacteristic = nil
        statusCharacteristic = nil
        pairingTokenAwaitingStatus = false
        sequence = 0
        commandQueue.removeAll()
        inFlightCommand = nil
        pointerRemainderX = 0
        pointerRemainderY = 0
    }

    private func fail(_ message: String) {
        if let peripheral = activePeripheral {
            activePeripheral = nil
            central.cancelPeripheralConnection(peripheral)
        }
        clearCharacteristics()
        pairingAttemptPending = false
        pairingWritePending = false
        pairingReadPending = false
        setState(.failed(message))
    }

    private func sendArrow(_ direction: WindowsArrow) {
        enqueue(opcode: .arrow, payload: Data([direction.rawValue]))
    }

    private func sendKey(_ key: WindowsKey) {
        enqueue(opcode: .keyPress, payload: Data([key.rawValue]))
    }

    private func sendMediaKey(_ key: WindowsMediaKey) {
        enqueue(opcode: .mediaKey, payload: Data([key.rawValue]))
    }

    private func enqueue(opcode: WindowsOpcode, payload: Data) {
        guard state == .connected,
              payload.count <= Self.maximumTextChunk,
              let token,
              token.count == 32 else { return }
        guard sequence < UInt32.max else {
            fail("The Bluetooth command session needs to reconnect before more input can be sent.")
            return
        }

        sequence += 1
        var frame = Data([Self.protocolVersion, opcode.rawValue])
        var littleSequence = sequence.littleEndian
        withUnsafeBytes(of: &littleSequence) { frame.append(contentsOf: $0) }
        frame.append(payload)
        let key = SymmetricKey(data: token)
        let tag = HMAC<SHA256>.authenticationCode(for: frame, using: key)
        frame.append(contentsOf: tag.prefix(Self.authenticationTagLength))
        commandQueue.append(frame)
        writeNextCommandIfNeeded()
    }

    private func enqueuePointer(_ payload: Data) {
        guard state == .connected, let token, token.count == 32 else { return }
        if let last = commandQueue.last,
           last.count == Self.frameHeaderLength + Self.pointerPayloadLength + Self.authenticationTagLength,
           last[1] == WindowsOpcode.pointerMove.rawValue {
            let oldX = Int16(bitPattern: UInt16(last[6]) | (UInt16(last[7]) << 8))
            let oldY = Int16(bitPattern: UInt16(last[8]) | (UInt16(last[9]) << 8))
            let incomingX = Int16(bitPattern: UInt16(payload[0]) | (UInt16(payload[1]) << 8))
            let incomingY = Int16(bitPattern: UInt16(payload[2]) | (UInt16(payload[3]) << 8))
            let limit = Int(Self.maximumPointerDelta)
            let newX = Int16(max(-limit, min(limit, Int(oldX) + Int(incomingX))))
            let newY = Int16(max(-limit, min(limit, Int(oldY) + Int(incomingY))))
            var merged = Data(last.dropLast(Self.authenticationTagLength))
            var littleX = newX.littleEndian
            var littleY = newY.littleEndian
            var mergedPayload = Data()
            withUnsafeBytes(of: &littleX) { mergedPayload.append(contentsOf: $0) }
            withUnsafeBytes(of: &littleY) { mergedPayload.append(contentsOf: $0) }
            merged.replaceSubrange(6..<10, with: mergedPayload)
            let tag = HMAC<SHA256>.authenticationCode(for: merged, using: SymmetricKey(data: token))
            merged.append(contentsOf: tag.prefix(Self.authenticationTagLength))
            commandQueue[commandQueue.count - 1] = merged
            return
        }
        enqueue(opcode: .pointerMove, payload: payload)
    }

    private func writeNextCommandIfNeeded() {
        guard state == .connected,
              inFlightCommand == nil,
              !commandQueue.isEmpty,
              let peripheral = activePeripheral,
              let characteristic = commandCharacteristic else { return }
        let frame = commandQueue.removeFirst()
        inFlightCommand = frame
        peripheral.writeValue(frame, for: characteristic, type: .withResponse)
    }

    private func handleStatus(_ data: Data) {
        guard data.count == 5, data[0] == Self.protocolVersion else {
            fail("The Windows receiver sent an unsupported Bluetooth status.")
            return
        }
        let isAdvertising = data[1] != 0
        let pairedPhoneIsSubscribed = data[2] != 0
        let isPaired = data[3] != 0
        let pairingStatus = data[4]

        guard isAdvertising else {
            fail("The Windows receiver stopped advertising. Reopen it and try again.")
            return
        }
        guard isPaired else {
            // A pre-pair notification can arrive after the token read; wait for the post-pair status read instead.
            if pairingTokenAwaitingStatus { return }
            if pairingStatus == WindowsPairingStatus.storageError.rawValue {
                fail("Windows could not securely read the saved pairing. Use Forget paired iPhone in the receiver and pair again.")
                return
            }
            if pairingStatus == WindowsPairingStatus.locked.rawValue {
                fail("Too many incorrect codes. Wait one minute, then try again.")
                return
            }
            if pairingStatus == WindowsPairingStatus.codeExpired.rawValue {
                fail("The pairing code expired. Use the current code shown in the Windows receiver and reconnect.")
                return
            }
            if !tokenAccount.isEmpty { TokenStore.forget(tokenAccount) }
            token = nil
            pairingAttemptPending = false
            setState(.waitingForPairing)
            return
        }
        pairingTokenAwaitingStatus = false

        if token == nil, !tokenAccount.isEmpty {
            token = TokenStore.data(for: tokenAccount)
        }
        guard token != nil else {
            if pairingAttemptPending || pairingReadPending {
                setState(.connecting)
            } else {
                fail("This iPhone is paired in Windows, but its saved key is missing. Choose “Forget paired iPhone” in the Windows receiver, then pair again.")
            }
            return
        }
        if pairedPhoneIsSubscribed {
            if state != .connected { sequence = 0 }
            setState(.connected)
        } else {
            setState(.connecting)
        }
    }

    private func handlePairingValue(_ data: Data) {
        pairingReadPending = false
        guard data.count == 32 else {
            guard data.count == 2, data[0] == Self.protocolVersion else {
                fail("The Windows receiver returned an invalid pairing response.")
                return
            }
            pairingAttemptPending = false
            switch WindowsPairingStatus(rawValue: data[1]) {
            case .some(.idle), .some(.invalidCode):
                onPairingCodeRejected?()
                setState(.waitingForPairing)
            case .some(.locked):
                fail("Too many incorrect codes. Wait one minute, then try again.")
            case .some(.paired):
                fail("The Windows receiver already has a pairing. Choose “Forget paired iPhone” there before pairing again.")
            case .some(.codeExpired):
                fail("The pairing code expired. Use the current code shown in the Windows receiver and reconnect.")
            case .some(.storageError), nil:
                fail("Windows could not securely save the pairing. Check the receiver and try again.")
            }
            return
        }

        token = data
        pairingAttemptPending = false
        guard TokenStore.save(data, for: tokenAccount) else {
            token = nil
            fail("The iPhone couldn't securely save the pairing in Keychain. Check iPhone storage, choose “Forget paired iPhone” in Windows, then pair again.")
            return
        }
        pairingTokenAwaitingStatus = true
        setState(.connecting)
        guard let peripheral = activePeripheral, let statusCharacteristic else {
            fail("The Windows receiver did not provide connection status after pairing.")
            return
        }
        peripheral.readValue(for: statusCharacteristic)
    }
}

extension WindowsRemoteTransport: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        updateBluetoothMessage(for: central.state)
        guard central.state == .poweredOn else {
            if targetIdentifier != nil {
                setState(.failed("Bluetooth is unavailable. Turn it on in iPhone Settings, then retry."))
            }
            return
        }

        if let identifier = targetIdentifier {
            if let peripheral = central.retrievePeripherals(withIdentifiers: [identifier]).first {
                beginConnect(to: peripheral)
            } else {
                discoveryRequested = true
                scanForWindowsReceivers()
            }
        } else if discoveryRequested {
            scanForWindowsReceivers()
        }
    }

    func centralManager(
        _ central: CBCentralManager,
        didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any],
        rssi RSSI: NSNumber
    ) {
        if let targetIdentifier {
            guard peripheral.identifier == targetIdentifier else { return }
            let advertisedName = advertisementData[CBAdvertisementDataLocalNameKey] as? String
            targetName = peripheral.name ?? advertisedName ?? targetName
            beginConnect(to: peripheral)
            return
        }

        let advertisedName = advertisementData[CBAdvertisementDataLocalNameKey] as? String
        let name = peripheral.name ?? advertisedName ?? "Windows PC"
        discovered[peripheral.identifier] = WindowsRemoteDevice(id: peripheral.identifier, name: name, peripheral: peripheral)
        devices = discovered.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        onDevicesChanged?(devices)
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard peripheral.identifier == targetIdentifier else { return }
        activePeripheral = peripheral
        peripheral.delegate = self
        setState(.connecting)
        peripheral.discoverServices([Self.serviceUUID])
    }

    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        guard peripheral.identifier == targetIdentifier else { return }
        fail("Couldn't connect to \(targetName) over Bluetooth. Keep the receiver open and retry.")
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        guard peripheral.identifier == targetIdentifier, activePeripheral != nil else { return }
        activePeripheral = nil
        clearCharacteristics()
        setState(.failed("Bluetooth disconnected from \(targetName). Move closer to the PC, then retry."))
    }
}

extension WindowsRemoteTransport: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard peripheral.identifier == targetIdentifier else { return }
        if error != nil {
            fail("Couldn't read the Windows receiver's Bluetooth service.")
            return
        }
        guard let service = peripheral.services?.first(where: { $0.uuid == Self.serviceUUID }) else {
            fail("The selected Bluetooth device is not a Free My TV Windows receiver.")
            return
        }
        peripheral.discoverCharacteristics([Self.commandUUID, Self.pairingUUID, Self.statusUUID], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard peripheral.identifier == targetIdentifier else { return }
        if error != nil {
            fail("Couldn't load the Windows receiver's Bluetooth controls.")
            return
        }
        for characteristic in service.characteristics ?? [] {
            switch characteristic.uuid {
            case Self.commandUUID: commandCharacteristic = characteristic
            case Self.pairingUUID: pairingCharacteristic = characteristic
            case Self.statusUUID: statusCharacteristic = characteristic
            default: break
            }
        }
        guard commandCharacteristic != nil, pairingCharacteristic != nil, let statusCharacteristic else {
            fail("The Windows receiver is missing a required Bluetooth control.")
            return
        }
        peripheral.setNotifyValue(true, for: statusCharacteristic)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral.identifier == targetIdentifier, characteristic.uuid == Self.statusUUID else { return }
        if error != nil || !characteristic.isNotifying {
            fail("Couldn't subscribe to Windows connection status. Check Bluetooth pairing and retry.")
            return
        }
        peripheral.readValue(for: characteristic)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral.identifier == targetIdentifier else { return }
        if error != nil {
            fail("Bluetooth could not read the Windows receiver. Check that the phone is paired, then retry.")
            return
        }
        guard let value = characteristic.value else { return }
        if characteristic.uuid == Self.statusUUID {
            handleStatus(value)
        } else if characteristic.uuid == Self.pairingUUID, pairingReadPending {
            handlePairingValue(value)
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral.identifier == targetIdentifier else { return }
        if characteristic.uuid == Self.pairingUUID, pairingWritePending {
            pairingWritePending = false
            if error != nil {
                pairingAttemptPending = false
                fail("Couldn't submit the pairing code. Check Bluetooth and try again.")
                return
            }
            guard let pairingCharacteristic else { return }
            pairingReadPending = true
            peripheral.readValue(for: pairingCharacteristic)
            return
        }
        guard characteristic.uuid == Self.commandUUID, inFlightCommand != nil else { return }
        if error != nil {
            fail("Windows rejected a remote command. Reconnect and try again.")
            return
        }
        inFlightCommand = nil
        writeNextCommandIfNeeded()
    }
}
