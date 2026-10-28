import SwiftUI
import UIKit

/// Swipe to move a TV's focus or the PC pointer; tap selects/clicks and holding performs a long press/right click.
struct Touchpad: View {
    let remote: TVRemote

    /// WHY: about a fingertip and a half; shorter steps made one swipe skip tiles, longer ones felt sluggish.
    private let step: CGFloat = 46
    /// WHY: movement under this is still a tap (fingers wobble a little when tapping).
    private let tapSlop: CGFloat = 12
    /// WHY: matches the TV's own long-press threshold (RemoteServer sends a 600 ms hold).
    private let holdSeconds = 0.55

    @State private var anchor: CGPoint?
    @State private var moved = false
    @State private var holdTask: Task<Void, Never>?
    @State private var touch: CGPoint?
    @State private var lastPointerLocation: CGPoint?
    @State private var pulse = false

    private let haptic = UIImpactFeedbackGenerator(style: .light)
    private let selectHaptic = UIImpactFeedbackGenerator(style: .medium)

    var body: some View {
        GeometryReader { geo in
            ZStack {
                RoundedRectangle(cornerRadius: 32, style: .continuous)
                    .fill(LinearGradient(colors: [Color(white: 0.17), Color(white: 0.11)], startPoint: .top, endPoint: .bottom))
                    .overlay(RoundedRectangle(cornerRadius: 32, style: .continuous).strokeBorder(.white.opacity(0.08)))
                if let touch {
                    Circle()
                        .fill(RadialGradient(colors: [.white.opacity(0.22), .clear], center: .center, startRadius: 0, endRadius: 70))
                        .frame(width: 140, height: 140)
                        .position(touch)
                        .scaleEffect(pulse ? 0.92 : 1)
                        .allowsHitTesting(false)
                }
                VStack(spacing: 6) {
                    Image(systemName: "hand.draw")
                        .font(.system(size: 26, weight: .light))
                    Text(remote.isWindowsPC ? "Swipe to move pointer · Tap to click" : "Swipe to move · Tap to select")
                        .font(.footnote)
                }
                .foregroundStyle(.white.opacity(touch == nil ? 0.28 : 0))
                .animation(.easeOut(duration: 0.2), value: touch == nil)
            }
            .contentShape(Rectangle())
            .gesture(drag)
            .accessibilityElement()
            .accessibilityLabel("Touchpad")
            .accessibilityHint("Swipe to move, double-tap to select")
            .accessibilityAction(named: "Up") { remote.press(.up) }
            .accessibilityAction(named: "Down") { remote.press(.down) }
            .accessibilityAction(named: "Left") { remote.press(.left) }
            .accessibilityAction(named: "Right") { remote.press(.right) }
            .accessibilityAction { remote.press(.center) }
            .frame(width: geo.size.width, height: geo.size.height)
        }
    }

    private var drag: some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { v in
                touch = v.location
                if anchor == nil {
                    anchor = v.startLocation
                    lastPointerLocation = v.startLocation
                    moved = false
                    haptic.prepare()
                    selectHaptic.prepare()
                    holdTask = Task { @MainActor in
                        try? await Task.sleep(for: .seconds(holdSeconds))
                        guard !Task.isCancelled, !moved else { return }
                        moved = true   // a hold is not also a tap
                        selectHaptic.impactOccurred(intensity: 1)
                        remote.click(right: true)
                    }
                }
                if remote.isWindowsPC {
                    if let lastPointerLocation {
                        remote.movePointer(
                            deltaX: v.location.x - lastPointerLocation.x,
                            deltaY: v.location.y - lastPointerLocation.y
                        )
                    }
                    lastPointerLocation = v.location
                    let totalX = v.location.x - v.startLocation.x
                    let totalY = v.location.y - v.startLocation.y
                    if abs(totalX) > tapSlop || abs(totalY) > tapSlop {
                        moved = true
                        holdTask?.cancel()
                    }
                    return
                }
                guard let a = anchor else { return }
                let dx = v.location.x - a.x, dy = v.location.y - a.y
                if abs(dx) > tapSlop || abs(dy) > tapSlop { holdTask?.cancel() }
                // One move per step along the main direction; the anchor follows so the next step counts afresh.
                if max(abs(dx), abs(dy)) >= step {
                    moved = true
                    let key: Key = abs(dx) > abs(dy) ? (dx > 0 ? .right : .left) : (dy > 0 ? .down : .up)
                    remote.press(key)
                    haptic.impactOccurred()
                    if abs(dx) > abs(dy) {
                        anchor = CGPoint(x: a.x + (dx > 0 ? step : -step), y: v.location.y)
                    } else {
                        anchor = CGPoint(x: v.location.x, y: a.y + (dy > 0 ? step : -step))
                    }
                }
            }
            .onEnded { v in
                holdTask?.cancel()
                let travel = hypot(v.location.x - v.startLocation.x, v.location.y - v.startLocation.y)
                if !moved && travel < tapSlop {
                    remote.click()
                    selectHaptic.impactOccurred()
                    withAnimation(.easeOut(duration: 0.12)) { pulse = true }
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { pulse = false }
                }
                anchor = nil
                lastPointerLocation = nil
                withAnimation(.easeOut(duration: 0.25)) { touch = nil }
            }
    }
}

/// The ring of arrows with OK in the middle, for people who'd rather press than swipe. Holding an arrow repeats it.
struct ArrowPad: View {
    let remote: TVRemote

    var body: some View {
        GeometryReader { geo in
            let size = min(geo.size.width, geo.size.height)
            ZStack {
                Circle()
                    .fill(LinearGradient(colors: [Color(white: 0.19), Color(white: 0.12)], startPoint: .top, endPoint: .bottom))
                    .overlay(Circle().strokeBorder(.white.opacity(0.08)))
                arrow(.up, "chevron.up").offset(y: -size * 0.33)
                arrow(.down, "chevron.down").offset(y: size * 0.33)
                arrow(.left, "chevron.left").offset(x: -size * 0.33)
                arrow(.right, "chevron.right").offset(x: size * 0.33)
                RepeatButton(action: { remote.press(.center) }, longAction: { remote.longPress(.center) }, repeats: false) {
                    Circle()
                        .fill(Color(white: 0.26))
                        .overlay(Text("OK").font(.system(size: 20, weight: .semibold)))
                        .frame(width: size * 0.36, height: size * 0.36)
                }
                .accessibilityLabel("OK")
            }
            .frame(width: size, height: size)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func arrow(_ key: Key, _ symbol: String) -> some View {
        RepeatButton(action: { remote.press(key) }) {
            Image(systemName: symbol)
                .font(.system(size: 24, weight: .semibold))
                .frame(width: 72, height: 72)
                .contentShape(Rectangle())
        }
        .accessibilityLabel(String(describing: key).capitalized)
    }
}

/// A button that fires once on press and, while held, either repeats (arrows, volume) or fires a long press.
struct RepeatButton<Label: View>: View {
    let action: () -> Void
    var longAction: (() -> Void)? = nil
    var repeats = true
    @ViewBuilder let label: () -> Label

    /// WHY: the first repeat after a short pause, then about 8 per second: close to a TV remote's own key repeat.
    private let firstDelay = 0.4, interval = 0.12

    @State private var task: Task<Void, Never>?
    @State private var pressed = false
    @State private var firedLong = false
    private let haptic = UIImpactFeedbackGenerator(style: .light)

    var body: some View {
        label()
            .foregroundStyle(.white.opacity(pressed ? 0.6 : 0.95))
            .scaleEffect(pressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.1), value: pressed)
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        guard !pressed else { return }
                        pressed = true
                        firedLong = false
                        haptic.impactOccurred()
                        if repeats { action() }
                        task = Task { @MainActor in
                            try? await Task.sleep(for: .seconds(firstDelay))
                            if !repeats {
                                guard !Task.isCancelled, let longAction else { return }
                                firedLong = true
                                haptic.impactOccurred(intensity: 1)
                                longAction()
                                return
                            }
                            while !Task.isCancelled {
                                action()
                                try? await Task.sleep(for: .seconds(interval))
                            }
                        }
                    }
                    .onEnded { _ in
                        task?.cancel()
                        if !repeats && !firedLong { action() }
                        pressed = false
                    }
            )
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { action() }
    }
}
