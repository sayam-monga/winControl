import SwiftUI

@main
struct RemoteApp: App {
    @State private var remote = TVRemote()
    @State private var discovery = Discovery()

    var body: some Scene {
        WindowGroup {
            ContentView(remote: remote, discovery: discovery)
                .preferredColorScheme(.dark)
                .tint(Color(red: 1.0, green: 0.42, blue: 0.48))
        }
    }
}

struct ContentView: View {
    let remote: TVRemote
    let discovery: Discovery
    @Environment(\.scenePhase) private var phase
    @AppStorage("tab") private var tab = 0
    @State private var picking = false

    var body: some View {
        NavigationStack {
            Group {
                if remote.target == nil {
                    DevicePicker(remote: remote, discovery: discovery)
                        .navigationTitle("Choose a device")
                } else {
                    main
                }
            }
        }
        .onChange(of: phase) { _, now in
            // Back from the background: the link is usually gone (iOS suspends sockets), so reconnect at once.
            if now == .active, remote.target != nil, remote.state != .connected, remote.state != .enterCode { remote.reconnect() }
            if now == .background { remote.disconnect() }
        }
        .onAppear {
            if remote.target != nil { remote.reconnect() }
        }
    }

    private var main: some View {
        VStack(spacing: 0) {
            header
            if remote.state == .enterCode {
                PairingView(remote: remote)
            } else {
                TabView(selection: $tab) {
                    if remote.isWindowsPC {
                        RemoteView(remote: remote)
                            .tabItem { Label("PC Remote", systemImage: "desktopcomputer") }
                            .tag(0)
                    } else {
                        RemoteView(remote: remote)
                            .tabItem { Label("Remote", systemImage: "appletvremote.gen4.fill") }
                            .tag(0)
                        AppsView(remote: remote)
                            .tabItem { Label("Apps", systemImage: "square.grid.2x2.fill") }
                            .tag(1)
                    }
                }
            }
        }
        .toolbar(.hidden, for: .navigationBar)
        .sheet(isPresented: $picking) {
            NavigationStack {
                DevicePicker(remote: remote, discovery: discovery) { picking = false }
                    .navigationTitle("Choose a device")
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar { Button("Done") { picking = false } }
            }
        }
    }

    private var header: some View {
        VStack(spacing: 8) {
            HStack(spacing: 12) {
                Button { picking = true } label: {
                    HStack(spacing: 8) {
                        Circle().fill(statusColor).frame(width: 9, height: 9)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(remote.displayName).font(.headline).lineLimit(1)
                            Text(statusText).font(.caption).foregroundStyle(.secondary)
                        }
                        Image(systemName: "chevron.down").font(.caption.weight(.bold)).foregroundStyle(.secondary)
                    }
                    .foregroundStyle(.white)
                }
                .accessibilityLabel("\(remote.isWindowsPC ? "Windows PC" : "TV"): \(remote.displayName). \(statusText). Choose another device")
                Spacer()
                if !remote.isWindowsPC {
                    CircleButton(symbol: "power", label: "Power", tint: Color(red: 1, green: 0.38, blue: 0.36)) { remote.press(.power) }
                }
            }
            if case .failed(let message) = remote.state {
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: remote.isWindowsPC ? "bluetooth" : "wifi.exclamationmark")
                    Text(message).font(.footnote).frame(maxWidth: .infinity, alignment: .leading)
                    Button("Retry") { remote.reconnect() }.font(.footnote.weight(.semibold))
                }
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(white: 0.13)))
            }
        }
        .padding(.horizontal, 22)
        .padding(.top, 8)
        .padding(.bottom, 12)
    }

    private var statusColor: Color {
        switch remote.state {
        case .connected: return .green
        case .connecting, .enterCode: return .orange
        case .failed, .idle: return .red
        }
    }

    private var statusText: String {
        switch remote.state {
        case .connected: return remote.isWindowsPC ? "Connected over Bluetooth" : "Connected"
        case .connecting: return remote.isWindowsPC ? "Connecting over Bluetooth" : "Connecting"
        case .enterCode: return "Waiting for the code"
        case .failed, .idle: return "Not connected"
        }
    }
}
