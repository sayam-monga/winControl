# Windows BLE fix handoff

Updated 2026-10-29. This handoff captures the reported disconnect and the Windows-side source fix for pickup on the Windows PC.

## Pick up the Windows change

The continuation branch is `codex/windows-bluetooth-fallback`; it includes commit `22beb31` and follow-up Windows package and handoff updates. The Windows source compiled with the installed .NET SDK, and MSIX 1.0.2.0 was built and installed for the current Windows user. The fix sets `GattCharacteristic.ProtectionLevel` to `EncryptionRequired` on the iPhone pairing and status characteristics before using them, matching the encrypted-access permissions declared by the iPhone. Reopen the receiver and retry the Bluetooth connection.

## Windows build and installation

Observed Windows setup: Windows 11 Home Single Language build 26200, .NET SDK 8.0.427, Windows SDK 10.0.22621.0. The MediaTek Bluetooth adapter and an iPhone appear in Windows device inventory, but BLE roles and app behavior have not been checked. MSIX 1.0.2.0 and its public signing certificate were generated under `windows/FreeMyTVRemote/artifacts/`; those build artifacts are local and are not included in the Git handoff. Rebuild the package on Windows if needed.

The Windows source at `22beb31` compiled successfully with .NET. The 1.0.2.0 MSIX was built, signed, and installed for the current Windows user. An earlier `-Install` attempt returned `0x800B0109` because its development certificate was untrusted; that was resolved before the 1.0.1.0 install. The 1.0.2.0 app has not yet been reopened for Bluetooth verification.

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

1. Open `ios/FreeMyTVRemote.xcodeproj` in Xcode. Run `xcodebuild -list -project ios/FreeMyTVRemote.xcodeproj` to inspect the available scheme, choose the Apple development team in Signing & Capabilities, and build for the attached iPhone. The user reports that the app from commit `3002333` was built, signed, and installed; the current Mac checkout's Xcode build is not independently verified here.
2. Fix any compiler, signing, permission-prompt, or lifecycle issues in the current working tree. Pay particular attention to CoreBluetooth peripheral delegate callbacks, encrypted GATT read/write permissions, notifications with backpressure, app backgrounding, and target persistence across relaunch.
3. Install and launch the Windows MSIX using the instructions above. With both devices nearby, let Windows select its supported BLE role and record which path the app actually reports.
4. Pair once, then exercise arrows, pointer movement, left/right click, text, enter/back, volume, mute, and play/pause. Verify disconnect, retry/reconnect, and pairing revocation. Check both role directions if adapters that support and do not support Windows peripheral mode are available.
5. Confirm TV discovery, pairing, buttons, and app launching still work over the existing TV path.
6. Update `behaviors.md` and `state.md` with only actions and results actually observed. Keep unrun or blocked checks explicitly unverified.

Current observed verification: Windows x64 build succeeded with 3 existing CA1416 platform-availability warnings and 0 errors. The 1.0.2.0 MSIX pack/sign succeeded and the install script reported success for the current Windows user. An earlier app build shown in the user's screenshot reported that BLE advertising stopped while Bluetooth was reported on; 1.0.1.0 added the Windows client scan on advertising failure. The user later reported a fallback disconnect; 1.0.2.0 adds encrypted GATT access requests but has not yet been reopened or tested. Pairing, command delivery, reconnect, and TV behavior remain untested.

## Main files changed for INC3

- `windows/FreeMyTVRemote/Bluetooth/PcRemoteGattClient.cs` — iPhone discovery, GATT connection/subscriptions, pairing token receipt, and authenticated command dispatch.
- `windows/FreeMyTVRemote/Security/PairingManager.cs` — DPAPI persistence for the iPhone-issued pairing token and Windows peer ID.
- `windows/FreeMyTVRemote/MainWindow.xaml` and `.xaml.cs` — role selection, fallback pairing entry, and receiver status.
- `ios/FreeMyTVRemote/Model/WindowsRemote.swift` — iPhone GATT peripheral, fallback pairing, notifications, and CoreBluetooth lifecycle.
- `ios/FreeMyTVRemote/Model/TVRemote.swift`, `Model/Discovery.swift`, `RemoteApp.swift`, and `Views/ConnectViews.swift` — target selection, fallback pairing display, stop/retry controls, and saved target handling.
- `windows/FreeMyTVRemote/Package.appxmanifest` — capability order required by MakeAppx schema validation.
- `windows/FreeMyTVRemote/build-package.ps1` — explicit restore source for Windows builds whose NuGet config has no package source.
