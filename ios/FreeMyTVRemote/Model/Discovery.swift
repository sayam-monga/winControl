import Foundation
import Network
import Observation

/// Where a TV is: found on the Wi-Fi by its announcement, or typed in as an address.
enum TVTarget: Hashable, Codable {
    case bonjour(name: String)
    case host(String)

    var label: String {
        switch self {
        case .bonjour(let name): return name
        case .host(let host): return host
        }
    }

    var endpoint: NWEndpoint {
        switch self {
        case .bonjour(let name):
            return .service(name: name, type: Discovery.serviceType, domain: "local.", interface: nil)
        case .host(let host):
            return .hostPort(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: Discovery.port)!)
        }
    }
}

/// A saved destination for the shared remote UI. Windows targets are keyed by CoreBluetooth's stable peripheral ID.
enum RemoteTarget: Hashable, Codable {
    case tv(TVTarget)
    case windowsPC(identifier: UUID, name: String)

    var label: String {
        switch self {
        case .tv(let target): return target.label
        case .windowsPC(_, let name): return name
        }
    }

    var tvTarget: TVTarget? {
        guard case .tv(let target) = self else { return nil }
        return target
    }
}

/// Finds TVs running Free My TV on the local network (they announce themselves over Bonjour).
@MainActor @Observable
final class Discovery {
    /// The TV side announces this type and port (RemoteLink.SERVICE_TYPE, RemoteServer.PORT).
    nonisolated static let serviceType = "_freemytv._tcp"
    nonisolated static let port: UInt16 = 7345

    private(set) var found: [String] = []
    private(set) var searching = false
    private var browser: NWBrowser?

    func start() {
        guard browser == nil else { return }
        let b = NWBrowser(for: .bonjour(type: Self.serviceType, domain: nil), using: .tcp)
        b.browseResultsChangedHandler = { [weak self] results, _ in
            let names = results.compactMap { result -> String? in
                if case .service(let name, _, _, _) = result.endpoint { return name }
                return nil
            }.sorted()
            Task { @MainActor in self?.found = names }
        }
        b.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                switch state {
                case .ready: self?.searching = true
                case .failed, .cancelled: self?.searching = false
                default: break
                }
            }
        }
        b.start(queue: .main)
        browser = b
    }

    func stop() {
        browser?.cancel()
        browser = nil
        searching = false
    }
}
