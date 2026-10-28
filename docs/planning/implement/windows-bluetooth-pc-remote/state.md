# Development status and Windows handoff

Updated 2026-10-29. Repository: [sayam-monga/winControl](https://github.com/sayam-monga/winControl). The initial published source baseline is commit `cc9d32a`; clone the latest `main` branch to continue.

## What is implemented

- **INC1 source:** Windows receiver UI, custom BLE GATT service, six-digit first-pairing code, rate-limited attempts, current-user DPAPI token storage, authenticated/versioned command protocol, adapter-role detection, and allowlisted Windows input dispatch.
- **INC2 source:** iPhone CoreBluetooth connection to the Windows GATT server, Windows PC target in the companion app, Keychain token storage, and routing for keyboard, pointer, navigation, and media actions. The existing TV TCP/Bonjour connection remains in the app.
- Requirements, BLE design/protocol, implementation increments, behavior coverage, Windows setup, and root build instructions are in this repository.
- The full iOS app built successfully with Xcode in the previous development session, and the app was installed on the user's iPhone. That does **not** verify pairing or control against a Windows PC.

## What is not verified or not implemented

- The Windows project has **not** been built or run on Windows. No Windows-built MSIX is committed.
- Windows/iPhone BLE discovery, system Bluetooth bonding, first pairing, saved-token reconnect, command delivery, disconnect recovery, and PC input behavior still need device verification.
- A previous macOS cross-publish produced a standalone x64 executable, but it was not Windows-tested and is not part of this repo. It is not the installable app: the BLE GATT server uses Windows APIs that require package identity. Use the MSIX build below.
- **INC3 is not implemented:** iPhone advertises as the GATT peripheral while Windows scans and connects. This is the fallback for PCs whose Bluetooth adapter cannot act as a peripheral.
- **INC4 is open:** finish cross-path UI/setup details, record review and device evidence, and close out the behavior/requirements status after testing.

The current path is Windows GATT server (peripheral) + iPhone GATT client (central). It needs no shared Wi-Fi or internet, but the PC adapter and its driver must support BLE peripheral mode. The receiver must stay open and the iPhone app must remain in the foreground. Windows may block input into apps running as administrator.

## First steps on Windows

Use Windows 10 version 1703 or later, or Windows 11. The build script targets x64 by default and also supports ARM64. Install:

- .NET 8 SDK.
- Windows 10/11 SDK containing `MakeAppx.exe` and `SignTool.exe` (the script searches under `%ProgramFiles(x86)%\Windows Kits\10\bin`).
- A BLE adapter/driver with peripheral-role support to test the current INC1/INC2 connection path.

In PowerShell:

```powershell
git clone https://github.com/sayam-monga/winControl.git
cd winControl\windows\FreeMyTVRemote
.\build-package.ps1
```

The default build creates `artifacts\FreeMyTVRemote-x64.msix` and `artifacts\FreeMyTVRemote.cer`. ARM64 can be built with `-Architecture arm64`.

To install for the current Windows user, run `.\build-package.ps1 -Install`. **This changes the current user's certificate store and installs the app:** the script creates or reuses a local development signing certificate, trusts its public certificate under `CurrentUser\TrustedPeople`, then installs the MSIX with `Add-AppxPackage`. Explain those effects and get the user's approval before running this option on their PC. Public distribution needs a publisher-owned certificate.

Rebuilding/signing the iPhone app still requires macOS and Xcode; Windows Codex can work on its source but cannot perform that native iOS build locally.

## Device verification checklist

When a Windows PC and iPhone are available:

1. Launch the receiver and record whether it reports BLE peripheral support and begins advertising. It should show a six-digit code only when it is ready.
2. In the iPhone app, choose **Windows PC over Bluetooth**, find the receiver, complete any iOS Bluetooth-bond prompt, and pair with the displayed code.
3. Verify arrows/Enter/Escape, text entry, pointer move/click/right-click, volume, mute, and play/pause. Check that the receiver does not send commands from an unpaired phone.
4. Background/foreground the iPhone app, disconnect and reconnect, and check that the saved pairing resumes without re-entering the code. Test **Forget paired iPhone** and fresh pairing.
5. Verify the app explains an unsupported peripheral role clearly. Do not mark the fallback working until INC3 is implemented and tested.
6. Update `behaviors.md` and this file with the actual Windows version, adapter/driver, build result, and observed pass/fail evidence. Leave checks unpassed when no device test occurred.

## Next development path

First build the Windows MSIX and resolve any Windows-only compile, package, or runtime issues. Complete the INC1/INC2 device checks where hardware permits. Then prepare the INC3 behavior proposal for approval before implementing the iPhone-peripheral/Windows-central fallback. Reuse the current protocol, authentication, pairing limits, and input dispatcher; preserve the existing TV transport. INC4 is the final integration, documentation, review, and verified-status closeout.

## Prompt to resume in Codex on Windows

Copy this into a Codex task opened in the cloned repository:

> Continue development of `winControl` from the latest `main`. Read the root `README.md`, `windows/FreeMyTVRemote/README.md`, and the requirements, design, increments, behaviors, and Windows handoff in `docs/planning/implement/windows-bluetooth-pc-remote/`. Check the working tree and Windows/.NET/SDK/Bluetooth environment first. Build the Windows MSIX and fix any Windows-only build or packaging problems. Before running `build-package.ps1 -Install`, explain that it trusts a development certificate and installs the package for my Windows user, then wait for my approval. If the iPhone and BLE adapter are available, verify INC1/INC2 discovery, pairing, commands, and reconnect; record only results you actually observed in the behavior and state docs. Keep the current TV transport working. Next, prepare the INC3 behavior proposal for my approval and pause at each phase gate before implementing the iPhone-peripheral/Windows-central fallback. Do not claim device tests passed unless you ran them.
