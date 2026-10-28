# Windows-to-Mac handoff

Updated 2026-10-29. This handoff captures the Windows-side work and the next iPhone build/device pass.

## Carry the current work to Mac

The current checkout is on `main` at base commit `cc9d32a`, matching `origin/main` when work began. The changes described here are still uncommitted on the Windows machine, including the new `windows/FreeMyTVRemote/Bluetooth/PcRemoteGattClient.cs`. A fresh Mac clone from GitHub will not contain them. Continue in the same synced checkout or transfer the entire working tree, including untracked files, before switching machines. No commit or push has been made.

## Windows build and installation

Observed Windows setup: Windows 11 Home Single Language build 26200, .NET SDK 8.0.427, Windows SDK 10.0.22621.0. The MediaTek Bluetooth adapter and an iPhone appear in Windows device inventory, but BLE roles and app behavior have not been checked. The current x64 MSIX is version 1.0.1.0 at `windows/FreeMyTVRemote/artifacts/FreeMyTVRemote-x64.msix`; its public signing certificate is `windows/FreeMyTVRemote/artifacts/FreeMyTVRemote.cer`.

The Windows source compiled successfully with .NET. The 1.0.1.0 MSIX was built and signed. The user reports that it installed successfully after closing the running 1.0.0.0 app. An earlier `-Install` attempt returned `0x800B0109` because its development certificate was untrusted; that was resolved before the reported install. The app has not yet been reopened for Bluetooth verification.

From an **Administrator PowerShell** window on this Windows PC, trust this local development certificate in the Local Computer Trusted People store:

```powershell
Import-Certificate -FilePath "C:\winctrl\winControl\windows\FreeMyTVRemote\artifacts\FreeMyTVRemote.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then, from a **normal PowerShell** window signed in as the Windows user who should run the app, install it:

```powershell
Add-AppxPackage -Path "C:\winctrl\winControl\windows\FreeMyTVRemote\artifacts\FreeMyTVRemote-x64.msix"
```

Launch **Free My TV Remote** from Start and leave it open. To rebuild the package, open PowerShell in `windows/FreeMyTVRemote` and run:

```powershell
./build-package.ps1
```

The script uses .NET 8, Windows SDK `MakeAppx.exe` and `SignTool.exe`, and the current user's development signing certificate. The script now supplies nuget.org as a process-only restore source; it does not change the user's NuGet configuration. After importing the latest exported certificate as above, run `./build-package.ps1 -Install` if you want it to rebuild, add the public certificate to the current user's Trusted People store, and install the package for that user. The earlier run did not satisfy this PC's trust policy by itself.

## Pairing and use

The receiver chooses its role at startup:

- **Windows GATT server:** If the adapter supports BLE peripheral mode, Windows advertises. In the iPhone app open **Windows PC over Bluetooth**, choose the PC, and enter the Windows-displayed code on the phone.
- **Windows GATT client fallback:** If the adapter cannot advertise as a BLE peripheral, Windows scans. Open the iPhone app's Windows connection screen and keep it foregrounded; the iPhone displays the code, which you enter in the Windows receiver.

For fallback pairing, use **Forget Windows pairing** on the iPhone to revoke its saved token. If Windows reports that the two saved pairings do not match, forget the pairing on both sides, reconnect, and pair again. Keep the receiver open and the iPhone app foregrounded during use. The existing TV connection continues to use its Wi-Fi/TCP path.

## Mac build and device verification

1. Open `ios/FreeMyTVRemote.xcodeproj` in Xcode. Run `xcodebuild -list -project ios/FreeMyTVRemote.xcodeproj` to inspect the available scheme, choose the Apple development team in Signing & Capabilities, and build for the attached iPhone. The INC3 peripheral-role additions have not yet been compiled with Xcode.
2. Fix any compiler, signing, permission-prompt, or lifecycle issues in the current working tree. Pay particular attention to CoreBluetooth peripheral delegate callbacks, encrypted GATT read/write permissions, notifications with backpressure, app backgrounding, and target persistence across relaunch.
3. Install and launch the Windows MSIX using the instructions above. With both devices nearby, let Windows select its supported BLE role and record which path the app actually reports.
4. Pair once, then exercise arrows, pointer movement, left/right click, text, enter/back, volume, mute, and play/pause. Verify disconnect, retry/reconnect, and pairing revocation. Check both role directions if adapters that support and do not support Windows peripheral mode are available.
5. Confirm TV discovery, pairing, buttons, and app launching still work over the existing TV path.
6. Update `behaviors.md` and `state.md` with only actions and results actually observed. Keep unrun or blocked checks explicitly unverified.

Current observed verification: Windows x64 build succeeded with 3 existing CA1416 platform-availability warnings and 0 errors. The 1.0.1.0 MSIX pack/sign succeeded and produced a 78,408,959-byte package; the user reports successful installation after closing 1.0.0.0. An earlier app build shown in the user's screenshot started but reported that BLE advertising stopped while Bluetooth was reported on. The 1.0.1.0 update switches to the Windows client scan after an advertising failure, but this behavior has not been verified. Pairing, command delivery, reconnect, and TV behavior remain untested. The INC3 iPhone source has not been built on Mac yet.

## Main files changed for INC3

- `windows/FreeMyTVRemote/Bluetooth/PcRemoteGattClient.cs` — iPhone discovery, GATT connection/subscriptions, pairing token receipt, and authenticated command dispatch.
- `windows/FreeMyTVRemote/Security/PairingManager.cs` — DPAPI persistence for the iPhone-issued pairing token and Windows peer ID.
- `windows/FreeMyTVRemote/MainWindow.xaml` and `.xaml.cs` — role selection, fallback pairing entry, and receiver status.
- `ios/FreeMyTVRemote/Model/WindowsRemote.swift` — iPhone GATT peripheral, fallback pairing, notifications, and CoreBluetooth lifecycle.
- `ios/FreeMyTVRemote/Model/TVRemote.swift`, `Model/Discovery.swift`, `RemoteApp.swift`, and `Views/ConnectViews.swift` — target selection, fallback pairing display, stop/retry controls, and saved target handling.
- `windows/FreeMyTVRemote/Package.appxmanifest` — capability order required by MakeAppx schema validation.
- `windows/FreeMyTVRemote/build-package.ps1` — explicit restore source for Windows builds whose NuGet config has no package source.
