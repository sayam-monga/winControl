import SwiftUI

/// The remote: touchpad (or arrows), the remote's buttons, volume, typing.
struct RemoteView: View {
    let remote: TVRemote
    @AppStorage("useArrows") private var useArrows = false
    @State private var typing = false

    var body: some View {
        VStack(spacing: 18) {
            HStack {
                Picker("Control", selection: $useArrows) {
                    Text("Touchpad").tag(false)
                    Text("Arrows").tag(true)
                }
                .pickerStyle(.segmented)
                .frame(width: 200)
                Spacer()
                CircleButton(symbol: "keyboard", label: "Type") { typing = true }
            }

            Group {
                if useArrows { ArrowPad(remote: remote) } else { Touchpad(remote: remote) }
            }
            .frame(maxHeight: .infinity)

            HStack(spacing: 0) {
                CircleButton(symbol: "chevron.backward", label: "Back", size: 64) { remote.press(.back) }
                Spacer()
                CircleButton(symbol: "house.fill", label: "Home", size: 64) { remote.press(.home) }
                Spacer()
                CircleButton(symbol: "playpause.fill", label: "Play or pause", size: 64) { remote.press(.playPause) }
            }

            HStack(spacing: 14) {
                CircleButton(symbol: "speaker.slash.fill", label: "Mute", size: 56) { remote.press(.mute) }
                VolumeRocker(remote: remote)
                CircleButton(symbol: "line.3.horizontal", label: "Menu", size: 56) { remote.press(.menu) }
            }
        }
        .padding(.horizontal, 22)
        .padding(.bottom, 12)
        .sheet(isPresented: $typing) {
            KeyboardSheet(remote: remote)
                .presentationDetents([.height(230)])
                .presentationDragIndicator(.visible)
        }
    }
}

/// A round dark button with an SF Symbol, like the Apple TV remote's.
struct CircleButton: View {
    let symbol: String
    let label: String
    var size: CGFloat = 44
    var tint: Color = .white
    let action: () -> Void

    var body: some View {
        RepeatButton(action: action, repeats: false) {
            Image(systemName: symbol)
                .font(.system(size: size * 0.36, weight: .semibold))
                .foregroundStyle(tint)
                .frame(width: size, height: size)
                .background(Circle().fill(Color(white: 0.17)))
                .overlay(Circle().strokeBorder(.white.opacity(0.07)))
                .contentShape(Circle())
        }
        .accessibilityLabel(label)
    }
}

/// Volume down / up in one pill; holding either side keeps changing it.
struct VolumeRocker: View {
    let remote: TVRemote

    var body: some View {
        HStack(spacing: 0) {
            RepeatButton(action: { remote.press(.volumeDown) }) {
                Image(systemName: "minus").frame(maxWidth: .infinity, maxHeight: .infinity).contentShape(Rectangle())
            }
            .accessibilityLabel("Volume down")
            Image(systemName: "speaker.wave.2.fill")
                .font(.system(size: 14))
                .foregroundStyle(.white.opacity(0.4))
                .accessibilityHidden(true)
            RepeatButton(action: { remote.press(.volumeUp) }) {
                Image(systemName: "plus").frame(maxWidth: .infinity, maxHeight: .infinity).contentShape(Rectangle())
            }
            .accessibilityLabel("Volume up")
        }
        .font(.system(size: 20, weight: .semibold))
        .frame(height: 56)
        .frame(maxWidth: .infinity)
        .background(Capsule().fill(Color(white: 0.17)))
        .overlay(Capsule().strokeBorder(.white.opacity(0.07)))
    }
}

/// Types into the text field that has focus on the TV (a search box, a login). Each letter is sent as you type it.
struct KeyboardSheet: View {
    let remote: TVRemote
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Type on the TV")
                .font(.headline)
            Text("Select a search box on the TV first, then type here.")
                .font(.footnote)
                .foregroundStyle(.secondary)
            HStack(spacing: 10) {
                TextField("Search, address, password…", text: $text)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .focused($focused)
                    .submitLabel(.go)
                    .onSubmit {
                        remote.press(.enter)
                        focused = true
                    }
                    .padding(.horizontal, 14)
                    .frame(height: 48)
                    .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(white: 0.14)))
                CircleButton(symbol: "delete.left", label: "Delete", size: 48) { remote.press(.delete) }
            }
        }
        .padding(22)
        .onAppear { focused = true }
        .onChange(of: text) { old, new in send(from: old, to: new) }
    }

    /// Sends only what changed: new letters as text, removed letters as Delete presses.
    private func send(from old: String, to new: String) {
        let common = zip(old, new).prefix { $0 == $1 }.count
        let removed = old.count - common
        for _ in 0..<removed { remote.press(.delete) }
        let added = String(new.dropFirst(common))
        if !added.isEmpty { remote.type(added) }
    }
}
