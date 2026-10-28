# Free My TV Remote for Windows

This is the Windows receiver source for controlling a Windows PC from the Free My TV iPhone companion app over Bluetooth Low Energy. It does not use Wi-Fi or an internet relay.

**Implementation status:** INC1/INC2 provide the Windows GATT server and iPhone central; the approved INC3 fallback now has Windows GATT client and iPhone peripheral source paths. The Windows x64 app compiles and packages successfully. Version 1.0.1.0 is signed and the user reports successful installation after closing version 1.0.0.0. A screenshot of the earlier build showed advertising stop while Bluetooth was reported on; version 1.0.1.0 starts the iPhone compatibility scan when server advertising fails. The current INC3 iPhone changes still need an Xcode build. No BLE discovery, pairing, input, or reconnect behavior has been verified on devices.

The executable is not the installable Bluetooth app: the Bluetooth APIs require Windows package identity and the `bluetooth` capability. Build the signed MSIX on Windows with the Windows SDK using `build-package.ps1`. The build was completed with .NET SDK 8.0.427 and Windows SDK 10.0.22621.0.

## Connection paths

The receiver checks whether its Bluetooth adapter supports BLE peripheral mode. If it does, Windows advertises and the iPhone finds it in the device picker. If the adapter cannot advertise or the Windows GATT service stops advertising, Windows scans as a GATT client; open the iPhone app's Windows connection screen and keep it in the foreground so it can advertise. In that fallback, the iPhone displays the six-digit pairing code and you enter it in the Windows receiver. The normal Windows-server path displays the code on Windows and you enter it on the iPhone.

The receiver needs to stay open while you use the phone. It requests no administrator privileges. Windows can block injected input in apps running as administrator.

## Build and install on Windows

Use a Windows 10 PC (version 1703 or later) or Windows 11 with an x64 or ARM64 processor, the .NET 8 SDK, and Windows 10/11 SDK installed. The app is packaged as MSIX because Windows requires package identity and the `bluetooth` capability for these GATT APIs.

From PowerShell in this folder:

```powershell
./build-package.ps1
```

The script creates a self-contained x64 package by default. Use `./build-package.ps1 -Architecture arm64` for ARM64. It signs `artifacts/FreeMyTVRemote-x64.msix` (or `FreeMyTVRemote-arm64.msix`) with a local development certificate and exports `artifacts/FreeMyTVRemote.cer`.

An earlier install attempt returned `0x800B0109` because Windows did not trust that package's development certificate. The latest package has a newly exported development certificate. To trust this local development certificate machine-wide, open PowerShell as Administrator and run:

```powershell
Import-Certificate `
  -FilePath "C:\winctrl\winControl\windows\FreeMyTVRemote\artifacts\FreeMyTVRemote.cer" `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

This trusts the development signing certificate for packages on this PC. Then, from a normal PowerShell window under the Windows user who should use the app, install the already-built package:

```powershell
Add-AppxPackage -Path "C:\winctrl\winControl\windows\FreeMyTVRemote\artifacts\FreeMyTVRemote-x64.msix"
```

The package is installed for that Windows user, not all users. Launch **Free My TV Remote** from Start. To rebuild, use `./build-package.ps1`; to let the script also import to the current user's Trusted People store and install for that user, run:

```powershell
./build-package.ps1 -Install
```

The machine-wide trust step requires administrator rights; the package install does not. The development certificate is only for local builds; a public release needs a publisher-owned signing certificate.

## Pairing and use

1. Open Free My TV Remote on the PC and leave its window open. It selects the server or client role from the adapter's peripheral-role support.
2. In server mode, choose the PC in the iPhone app's **Windows PC over Bluetooth** section and enter the Windows code on the iPhone. In fallback mode, open the iPhone app's Windows connection screen, enter the iPhone-displayed code in the Windows receiver, and keep the phone screen open.
3. Keep the iPhone app in the foreground and the Windows receiver open while controlling the PC. The same Wi-Fi network and an internet connection are not required.
4. In fallback mode, use **Forget Windows pairing** on the iPhone to revoke its saved token. If Windows and iPhone report mismatched saved pairing data, forget it on both devices, reconnect, and pair again.

The service and pairing characteristics require an encrypted Bluetooth link. The first secure GATT access may also ask iOS to confirm Bluetooth pairing. The six-digit app code authorizes the remote-control token; the iPhone stores it in Keychain, and Windows stores the token and paired Bluetooth device ID encrypted with current-user DPAPI. Pairing reads and commands are bound to the device ID from the GATT session that entered the code.

## Version 1 GATT protocol

The protocol contract uses UUIDs fixed in `Protocol/RemoteProtocol.cs` and `docs/planning/implement/windows-bluetooth-pc-remote/design.md`. The Windows x64 source compiles; the full iPhone/Windows exchange has not been verified on devices:

| GATT item | UUID | Properties |
|---|---|---|
| Service | `6D4F0001-4A2B-4B91-A6D7-31B7C2E10510` | Advertised service |
| Command | `6D4F0002-4A2B-4B91-A6D7-31B7C2E10510` | Windows server: encrypted write with response; iPhone fallback: authenticated notification |
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
