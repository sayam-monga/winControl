import SwiftUI
import UIKit

/// Picks a TV on Wi-Fi or a Windows PC over Bluetooth.
struct DevicePicker: View {
    let remote: TVRemote
    let discovery: Discovery
    var onDone: () -> Void = {}
    @State private var address = ""

    var body: some View {
        List {
            Section {
                if discovery.found.isEmpty {
                    HStack(spacing: 12) {
                        ProgressView()
                        Text("Looking for TVs on this Wi-Fi…").foregroundStyle(.secondary)
                    }
                }
                ForEach(discovery.found, id: \.self) { name in
                    Button {
                        remote.connect(to: .bonjour(name: name))
                        onDone()
                    } label: {
                        HStack {
                            Image(systemName: "tv").frame(width: 28)
                            Text(name)
                            Spacer()
                            if remote.target == .tv(.bonjour(name: name)) { Image(systemName: "checkmark").foregroundStyle(.tint) }
                        }
                    }
                }
            } header: {
                Text("On this Wi-Fi")
            } footer: {
                Text("Your TV shows up here when Free My TV's home screen is on.")
            }

            Section {
                HStack {
                    TextField("192.168.1.9", text: $address)
                        .keyboardType(.decimalPad)
                        .autocorrectionDisabled()
                    Button("Connect") {
                        remote.connect(to: .host(address.trimmingCharacters(in: .whitespaces)))
                        onDone()
                    }
                    .disabled(address.split(separator: ".").count != 4)
                }
            } header: {
                Text("Or type its address")
            } footer: {
                Text("On the TV: Settings → Network → your Wi-Fi shows the IP address.")
            }

            Section {
                if remote.windowsDevices.isEmpty {
                    HStack(spacing: 12) {
                        if remote.windowsDiscoveryMessage == nil { ProgressView() }
                        Image(systemName: "dot.radiowaves.left.and.right").foregroundStyle(.secondary)
                        Text(remote.windowsDiscoveryMessage ?? "Looking for Windows PCs over Bluetooth…")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }
                ForEach(remote.windowsDevices) { device in
                    Button {
                        remote.connect(toWindowsPC: device)
                        onDone()
                    } label: {
                        HStack {
                            Image(systemName: "desktopcomputer").frame(width: 28)
                            Text(device.name)
                            Spacer()
                            if let target = remote.target,
                               case .windowsPC(let id, _) = target,
                               id == device.id {
                                Image(systemName: "checkmark").foregroundStyle(.tint)
                            }
                        }
                    }
                }
            } header: {
                Text("Windows PC over Bluetooth")
            } footer: {
                Text("Keep the Windows receiver open nearby. Bluetooth works without the same Wi-Fi. Receiver adapters without BLE peripheral support will be supported in a later update.")
            }
        }
        .onAppear {
            discovery.start()
            remote.startWindowsDiscovery()
        }
        .onDisappear {
            discovery.stop()
            remote.stopWindowsDiscovery()
        }
    }
}

/// Typing the 4-digit code the TV shows.
struct PairingView: View {
    let remote: TVRemote
    @State private var code = ""
    @State private var shake = 0
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 22) {
            Image(systemName: remote.isWindowsPC ? "desktopcomputer" : "tv.and.mediabox")
                .font(.system(size: 44, weight: .light))
                .padding(.top, 30)
            Text("Pair with \(remote.displayName)")
                .font(.title2.weight(.semibold))
                .multilineTextAlignment(.center)
            Text(remote.isWindowsPC
                 ? "Type the six-digit code shown in the Windows receiver."
                 : "Type the 4-digit code shown on the TV.")
                .foregroundStyle(.secondary)
            ZStack {
                HStack(spacing: remote.isWindowsPC ? 8 : 14) {
                    ForEach(0..<remote.pairingCodeLength, id: \.self) { i in
                        let digit = i < code.count ? String(code[code.index(code.startIndex, offsetBy: i)]) : ""
                        Text(digit)
                            .font(.system(size: remote.isWindowsPC ? 28 : 34, weight: .semibold, design: .rounded))
                            .frame(width: remote.isWindowsPC ? 42 : 58, height: 70)
                            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(white: 0.15)))
                            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
                                .strokeBorder(i == code.count ? Color.accentColor : .white.opacity(0.08), lineWidth: i == code.count ? 2 : 1))
                    }
                }
                .modifier(Shake(amount: CGFloat(shake)))
                .onTapGesture { focused = true }
                TextField("", text: $code)
                    .keyboardType(.numberPad)
                    .textContentType(.oneTimeCode)
                    .focused($focused)
                    .opacity(0.02)
                    .frame(width: 1, height: 1)
                    .accessibilityLabel("Pairing code")
            }
            if remote.isWindowsPC {
                Text("The code expires after five minutes. Use the current code shown on the PC.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            } else {
                Button("Show a new code on the TV") {
                    code = ""
                    remote.newCode()
                }
                .font(.footnote)
            }
            Spacer()
        }
        .padding(.horizontal, 24)
        .onAppear { focused = true }
        .onChange(of: code) { _, new in
            let digits = String(new.filter { $0 >= "0" && $0 <= "9" }.prefix(remote.pairingCodeLength))
            if digits != new { code = digits; return }
            if digits.count == remote.pairingCodeLength { remote.submit(code: digits) }
        }
        .onChange(of: remote.codeRejected) { _, _ in
            UINotificationFeedbackGenerator().notificationOccurred(.error)
            withAnimation(.default) { shake += 1 }
            code = ""
        }
    }
}

/// Side-to-side wobble for a wrong code.
struct Shake: GeometryEffect {
    var amount: CGFloat
    var animatableData: CGFloat {
        get { amount }
        set { amount = newValue }
    }

    func effectValue(size: CGSize) -> ProjectionTransform {
        ProjectionTransform(CGAffineTransform(translationX: 10 * sin(amount * .pi * 4), y: 0))
    }
}
