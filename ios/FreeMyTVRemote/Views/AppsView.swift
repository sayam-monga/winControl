import SwiftUI
import UIKit

/// The TV's apps as cards; tap one to open it on the TV.
struct AppsView: View {
    let remote: TVRemote
    private let columns = [GridItem(.flexible(), spacing: 14), GridItem(.flexible(), spacing: 14)]

    var body: some View {
        ScrollView {
            if remote.apps.isEmpty {
                VStack(spacing: 12) {
                    if remote.loadingApps {
                        ProgressView()
                    } else {
                        Image(systemName: "square.grid.2x2").font(.system(size: 34, weight: .light))
                        Text("No apps yet").font(.headline)
                        Text("The list appears once Free My TV's home screen has opened on the TV.")
                            .font(.footnote)
                            .multilineTextAlignment(.center)
                    }
                }
                .foregroundStyle(.secondary)
                .padding(.top, 80)
                .padding(.horizontal, 40)
            } else {
                LazyVGrid(columns: columns, spacing: 18) {
                    ForEach(remote.apps) { app in
                        Button {
                            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                            remote.launch(app)
                        } label: {
                            VStack(alignment: .leading, spacing: 7) {
                                Group {
                                    if let icon = app.icon {
                                        Image(uiImage: icon).resizable().aspectRatio(16 / 9, contentMode: .fill)
                                    } else {
                                        Rectangle().fill(Color(white: 0.16))
                                            .overlay(Text(app.name.prefix(1)).font(.title.weight(.semibold)))
                                    }
                                }
                                .aspectRatio(16 / 9, contentMode: .fit)
                                .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
                                .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.white.opacity(0.08)))
                                Text(app.name)
                                    .font(.footnote.weight(.medium))
                                    .foregroundStyle(.white.opacity(0.85))
                                    .lineLimit(1)
                            }
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Open \(app.name)")
                    }
                }
                .padding(.horizontal, 20)
                .padding(.bottom, 20)
            }
        }
        .refreshable { remote.requestApps() }
    }
}
