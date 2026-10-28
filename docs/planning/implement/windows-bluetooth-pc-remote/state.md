# Development status

Updated 2026-10-29 after the user reported a failed BLE connection from the iPhone.

- The Windows checkout is on `codex/windows-bluetooth-fallback`; it includes the Mac-side fix `22beb31`, the status-notification/read serialization fix, and updated handoff notes.
- INC1 and INC2 have source implementations. The approved INC3 iPhone-peripheral / Windows-client fallback now has source paths on both platforms. INC4 closeout remains open.
- Windows environment observed: Windows 11 Home Single Language, build 26200; .NET SDK 8.0.427; Windows SDK 10.0.22621.0. Windows lists a MediaTek Bluetooth adapter and an iPhone entry in its paired-device inventory. Peripheral-role support has not been queried by the app.
- The Windows source at `22beb31` built successfully with `dotnet build FreeMyTVRemote.csproj -c Release -r win-x64 --self-contained true -p:PlatformTarget=x64 -p:RestoreSources=https://api.nuget.org/v3/index.json` (3 existing CA1416 platform-availability warnings, 0 errors). MSIX 1.0.2.0 packed and signed successfully; `artifacts/FreeMyTVRemote-x64.msix` is 78,408,962 bytes and `artifacts/FreeMyTVRemote.cer` is 774 bytes.
- An earlier `build-package.ps1 -Install` attempt returned `0x800B0109` because Windows did not trust that package's certificate; after trusting the development certificate, the user installed 1.0.1.0. The current `build-package.ps1 -Install` run succeeded and reports MSIX 1.0.2.0 installed for this Windows user. The updated app has not yet been reopened for BLE verification.
- A fallback connection was attempted, but the user reports that the pairing-code screen disappeared and pairing did not resolve the disconnect. Their Windows status screenshot was clipped after “Cannot acc…”. No commands, reconnect, or TV regression behavior have been verified. Do not mark any behavior passed based on the adapter and paired-device inventory alone.
- User-provided screenshot on 2026-10-29 shows an earlier installed app build reporting that BLE advertising stopped while the user says Bluetooth is on. The app did not enter fallback mode in that screenshot. The Windows startup/error path starts the GATT client scan when server startup fails or advertising stops; the user installed 1.0.1.0 and the later 1.0.2.0 package, but fallback behavior has not been verified.
- The current INC3 iPhone source has not been built with Xcode in this Windows session. The Mac handoff lists the exact Xcode build and device checks needed next.
- The user reports that the iPhone fallback app was built, signed, and installed on an iPhone 16 Pro from commit `3002333`. Commit `22beb31` requests `GattProtectionLevel.EncryptionRequired` for the iPhone pairing and status characteristics before Windows subscribes or reads them. The Windows build and package now include this fix, but the connection retry has not been verified.
- On 2026-10-29, the latest Windows receiver source was changed to parse the status bytes carried by each GATT notification instead of issuing another status read. Status handling and pairing-token reads are serialized, and stale callbacks from replaced connections are ignored. The Windows source compiled in the .NET 8 SDK container with 0 errors and the same 3 CA1416 warnings. `Package.appxmanifest` now requests MSIX 1.0.3.0; that package has not been built or installed.
- Xcode's device manager confirms `io.freemytv.remote` version 1.0 (build 1) is installed on the connected iPhone 16 Pro. No iOS source changed in this fix, so no iPhone rebuild is needed.
- The exact runtime cause is not confirmed because the screenshot clipped the Windows exception. The redundant GATT status reads and potential overlapping token reads are a concrete source-level race addressed by the fix; the Windows BLE retry is still required to verify it.
- The TV transport files and protocol were not changed; `TVRemote.swift` and `Discovery.swift` now branch by the Windows target role. TV behavior has not been regression-tested after those changes.

## Next steps

1. Pull `codex/windows-bluetooth-fallback` on Windows and run `./build-package.ps1 -Install` to build/install MSIX 1.0.3.0.
2. Keep the iPhone app in the foreground, open its Windows PC connection screen, and retry. Record the full Windows error if it disconnects again; encrypted access and notification-read serialization are now in source.
3. If the connection succeeds, verify pairing, command inputs, disconnect/reconnect, and pairing revocation; also confirm the existing TV path still works.
4. Record only observed results in `behaviors.md` and update this state file. Then complete INC4 review and closeout.
