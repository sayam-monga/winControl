# winControl

A Windows receiver and iPhone companion app for controlling a Windows PC over Bluetooth Low Energy (BLE). After pairing, control works without the PC and iPhone sharing Wi-Fi or having an internet connection.

## Current status

Both BLE role directions have source implementations, and the iPhone fallback app was built and installed on the user's iPhone. The user reports that the pairing-code screen appears and later disappears; after manually pairing the phone and PC, the connection still drops. The Windows status showed a truncated “Cannot acc…” error. The Windows GATT client now requests encrypted access for the iPhone's protected pairing and status characteristics. The updated source compiled in a .NET 8 SDK container, but it has not been packaged or retried on Windows, so successful connection is still unverified.

The Windows x64 receiver builds and packages as an MSIX on Windows. Version 1.0.1.0 was built and signed with a local development certificate on 2026-10-29. A screenshot of an earlier installed build showed Windows BLE advertising stop while Bluetooth was reported on; this update switches to the iPhone compatibility scan when advertising fails. The user reports that version 1.0.1.0 installed successfully after closing version 1.0.0.0. BLE discovery, pairing, commands, and reconnect have not yet been verified on devices. Windows requires package identity and the `bluetooth` capability, so the standalone `.exe` is not the installable app. See [the Mac handoff](docs/planning/implement/windows-bluetooth-pc-remote/windows-to-mac-handoff.md) for the next steps.

## Build the Windows receiver

On Windows 10 (version 1703 or later) or Windows 11, install the .NET 8 SDK and Windows 10/11 SDK with `MakeAppx.exe` and `SignTool.exe`. Then open PowerShell in the project folder:

```powershell
cd windows/FreeMyTVRemote
.\build-package.ps1
```

To also import the development certificate into the current user's Trusted People store and install the package for that Windows user:

```powershell
.\build-package.ps1 -Install
```

Windows may also require this development certificate in the Local Computer Trusted People store. That one-time step requires an administrator PowerShell window; the MSIX itself is still installed for the Windows user who runs `Add-AppxPackage`. Follow [the Windows setup guide](windows/FreeMyTVRemote/README.md) before installing. Public distribution will require a publisher-owned signing certificate.

## Build the iPhone companion

The iOS app source is in `ios/`. Building and signing it requires macOS with Xcode. Open `ios/FreeMyTVRemote.xcodeproj`, select your Apple development team for device signing, and run it on your iPhone. The phone and PC connect directly over BLE; the app's existing TV discovery and controls continue to use the TV's Wi-Fi path.

## Pair and use

1. Open Free My TV Remote on Windows and leave it running. If Windows can advertise as a BLE peripheral, it displays a six-digit code and appears in the iPhone's Windows PC list. If it cannot, open the iPhone app's Windows connection screen; Windows scans for the iPhone and the code appears on the phone for entry in the Windows receiver.
2. Keep the iPhone app in the foreground and the Windows receiver open while controlling the PC.

Pairing uses an authenticated BLE link. The iPhone keeps its token in Keychain; Windows keeps its token encrypted for the current Windows user. The receiver dispatches an allowlist of keyboard, text, pointer, and media actions.

## Project documents

- [Requirements](docs/planning/implement/windows-bluetooth-pc-remote/requirements.md)
- [Design and BLE protocol](docs/planning/implement/windows-bluetooth-pc-remote/design.md)
- [Implementation increments](docs/planning/implement/windows-bluetooth-pc-remote/increments.md)
- [Behavior coverage and verification status](docs/planning/implement/windows-bluetooth-pc-remote/behaviors.md)
- [Current development status](docs/planning/implement/windows-bluetooth-pc-remote/state.md)
- [Windows-to-Mac implementation and test handoff](docs/planning/implement/windows-bluetooth-pc-remote/windows-to-mac-handoff.md)
