# Free My TV Remote for Windows

This is the Windows receiver source for controlling a Windows PC from the Free My TV iPhone companion app over Bluetooth Low Energy. It does not use Wi-Fi or an internet relay.

**Implementation status:** INC1 provides the Windows receiver and INC2 adds the iPhone's Bluetooth central, pairing, and PC controls. The full iOS app builds successfully with Xcode. A self-contained x64 Windows executable was cross-published from macOS to `artifacts/FreeMyTVRemote-x64.exe` (about 178 MB). It has not been run on Windows.

The executable is not the installable Bluetooth app: the GATT server requires Windows package identity and the `bluetooth` capability. Build and install the signed MSIX on Windows with the Windows SDK using `build-package.ps1`; this workspace has no Windows SDK, so that package is not included yet.

## Current connection path

This first slice uses the Windows GATT server role. It works only when the PC's Bluetooth adapter and driver support BLE peripheral mode. The app checks that support at startup and explains when it is unavailable. The alternate iPhone-advertises / Windows-scans path is planned for the next compatibility increment.

The receiver needs to stay open while you use the phone. It requests no administrator privileges. Windows can block injected input in apps running as administrator.

## Build and install on Windows

Use a Windows 10 PC (version 1703 or later) or Windows 11 with an x64 or ARM64 processor, the .NET 8 SDK, and Windows 10/11 SDK installed. The app is packaged as MSIX because Windows requires package identity and the `bluetooth` capability for these GATT APIs.

From PowerShell in this folder:

```powershell
./build-package.ps1
```

The script creates a self-contained x64 package by default. Use `./build-package.ps1 -Architecture arm64` for ARM64. It signs `artifacts/FreeMyTVRemote-x64.msix` (or `FreeMyTVRemote-arm64.msix`) with a local development certificate and exports `artifacts/FreeMyTVRemote.cer`. To also trust that certificate for the current Windows user and install the x64 package, run:

```powershell
./build-package.ps1 -Install
```

Windows will show the package's requested Bluetooth access through its app capability. The development certificate is only for local builds; a public release needs a publisher-owned signing certificate.

## Pairing and use

1. Open Free My TV Remote on the PC and leave its window open. If the adapter supports the BLE peripheral role, the receiver advertises the custom GATT service and displays a six-digit code.
2. Open the iPhone app, choose **Windows PC over Bluetooth**, and select the receiver. Enter the code shown by Windows the first time.
3. Keep the iPhone app in the foreground and the Windows receiver open while controlling the PC. The same Wi-Fi network and an internet connection are not required.
4. If pairing was saved on Windows but the iPhone no longer has its Keychain token, choose **Forget paired iPhone** in the Windows app before pairing again.

The service and pairing characteristics require an encrypted Bluetooth link. The first secure GATT access may also ask iOS to confirm Bluetooth pairing. The six-digit app code authorizes the remote-control token; the iPhone stores it in Keychain, and Windows stores the token and paired Bluetooth device ID encrypted with current-user DPAPI. Pairing reads and commands are bound to the device ID from the GATT session that entered the code.

## Version 1 GATT protocol

The source-level protocol contract uses UUIDs fixed in `Protocol/RemoteProtocol.cs` and `docs/planning/implement/windows-bluetooth-pc-remote/design.md`; it has not yet been verified against a Windows build or iPhone client:

| GATT item | UUID | Properties |
|---|---|---|
| Service | `6D4F0001-4A2B-4B91-A6D7-31B7C2E10510` | Advertised service |
| Command | `6D4F0002-4A2B-4B91-A6D7-31B7C2E10510` | Encrypted write with response |
| Pairing | `6D4F0003-4A2B-4B91-A6D7-31B7C2E10510` | Encrypted read and write |
| Status | `6D4F0004-4A2B-4B91-A6D7-31B7C2E10510` | Encrypted read and notification |

Command frames contain a version byte, opcode byte, increasing 32-bit little-endian sequence number, up to six payload bytes, and an eight-byte truncated HMAC-SHA-256 tag. HMAC uses the 32-byte pairing token. Frames are limited to 20 bytes, the default ATT write payload size. Text is UTF-8 in chunks that end at character boundaries.

| Opcode | Payload | Windows action |
|---|---|---|
| `0x01` Arrow | `0` up, `1` down, `2` left, `3` right | Arrow key press |
| `0x02` Pointer move | Signed `dx:i16-le`, `dy:i16-le`; each from -2048 to 2048 | Relative pointer move |
| `0x03` Left click | Empty | Left mouse click |
| `0x04` Right click | Empty | Right mouse click |
| `0x10` Key press | `1` Enter, `2` Escape, `3` Backspace, `4` Windows key, `5` context menu | One allowlisted key press |
| `0x11` Text | 1–6 UTF-8 bytes | Unicode text input |
| `0x20` Media key | `1` play/pause, `2` volume up, `3` volume down, `4` mute | Windows media key |

The pairing characteristic accepts `version=1 | opcode=0x01 | six ASCII digits`. After a correct code, reading returns the 32-byte token; otherwise, it returns `[version, status]` (`0` idle, `1` incorrect code, `2` temporarily locked, `3` already paired, `4` code expired, `5` storage error). The code expires after five minutes; three incorrect attempts cause a one-minute lockout.

The status characteristic returns five bytes: `version | advertising | paired-peer-subscribed | paired | pairing-status`. The iPhone subscribes to status notifications before sending commands; a new subscription from the paired peer begins a fresh increasing-sequence session.

Unknown opcodes, invalid payload sizes, unsupported versions, invalid UTF-8, oversized frames, bad authentication tags, and repeated sequence numbers are ignored before any input is sent to Windows.
