# winControl

A Windows receiver and iPhone companion app for controlling a Windows PC over Bluetooth Low Energy (BLE). After pairing, control works without the PC and iPhone sharing Wi-Fi or having an internet connection.

## Current status

The first connection direction is implemented: Windows advertises a BLE GATT service and the iPhone app connects as the central. The PC's Bluetooth adapter and driver must support BLE peripheral mode. The reverse direction, where the iPhone advertises and Windows scans, is planned in INC3.

The Windows receiver source cross-published successfully on macOS, but it has not yet been built into an installable MSIX or tested on a Windows PC. Windows requires package identity for this Bluetooth GATT server, so the standalone `.exe` is not the installable app. The Windows build script packages and signs an MSIX using the Windows SDK.

## Build the Windows receiver

On Windows 10 (version 1703 or later) or Windows 11, install the .NET 8 SDK and Windows 10/11 SDK with `MakeAppx.exe` and `SignTool.exe`. Then open PowerShell in the project folder:

```powershell
cd windows/FreeMyTVRemote
.\build-package.ps1
```

To also trust the local development certificate and install the package for the current Windows user:

```powershell
.\build-package.ps1 -Install
```

Review the package and local certificate behavior in [the Windows setup guide](windows/FreeMyTVRemote/README.md) before installing. Public distribution will require a publisher-owned signing certificate.

## Build the iPhone companion

The iOS app source is in `ios/`. Building and signing it requires macOS with Xcode. Open `ios/FreeMyTVRemote.xcodeproj`, select your Apple development team for device signing, and run it on your iPhone. The phone and PC connect directly over BLE; the app's existing TV discovery and controls continue to use the TV's Wi-Fi path.

## Pair and use

1. Open Free My TV Remote on Windows and leave it running. A compatible adapter displays a six-digit pairing code.
2. In the iPhone app, choose **Windows PC over Bluetooth**, select the receiver, and enter the code.
3. Keep the iPhone app in the foreground and the Windows receiver open while controlling the PC.

Pairing uses an authenticated BLE link. The iPhone keeps its token in Keychain; Windows keeps its token encrypted for the current Windows user. The receiver dispatches an allowlist of keyboard, text, pointer, and media actions.

## Project documents

- [Requirements](docs/planning/implement/windows-bluetooth-pc-remote/requirements.md)
- [Design and BLE protocol](docs/planning/implement/windows-bluetooth-pc-remote/design.md)
- [Implementation increments](docs/planning/implement/windows-bluetooth-pc-remote/increments.md)
- [Behavior coverage and verification status](docs/planning/implement/windows-bluetooth-pc-remote/behaviors.md)
- [Current development status](docs/planning/implement/windows-bluetooth-pc-remote/state.md)
