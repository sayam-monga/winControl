# Development status

Updated 2026-10-29 on Windows 11.

- The working tree started from `main` at `cc9d32a` (`origin/main` at checkout). The current implementation and documentation edits are uncommitted; include the full working tree, including the new `PcRemoteGattClient.cs`, when moving this work to the Mac.
- INC1 and INC2 have source implementations. The approved INC3 iPhone-peripheral / Windows-client fallback now has source paths on both platforms. INC4 closeout remains open.
- Windows environment observed: Windows 11 Home Single Language, build 26200; .NET SDK 8.0.427; Windows SDK 10.0.22621.0. Windows lists a MediaTek Bluetooth adapter and an iPhone entry in its paired-device inventory. Peripheral-role support has not been queried by the app.
- The current Windows source built successfully with `dotnet build FreeMyTVRemote.csproj -c Release -r win-x64 --self-contained true -p:PlatformTarget=x64 -p:RestoreSources=https://api.nuget.org/v3/index.json` (3 existing CA1416 platform-availability warnings, 0 errors). The x64 MSIX version 1.0.1.0 then packed and signed successfully; `artifacts/FreeMyTVRemote-x64.msix` is 78,408,959 bytes and `artifacts/FreeMyTVRemote.cer` is 774 bytes.
- `build-package.ps1 -Install` was approved and attempted before the latest package rebuild. That package's certificate was imported into current-user Trusted People, but `Add-AppxPackage` returned `0x800B0109` (certificate chain not trusted). Importing that certificate into current-user Trusted Root did not fix the install; an attempt to trust it in Local Computer Trusted People returned Access Denied. The user later reports that MSIX 1.0.1.0 installed successfully after closing the running 1.0.0.0 app. The updated app has not yet been reopened for BLE verification.
- The app has not performed a BLE role check or Bluetooth session. Discovery, pairing, commands, reconnect, and TV regression behavior have not been run. Do not mark any behavior passed based on the adapter and paired-device inventory alone.
- User-provided screenshot on 2026-10-29 shows an earlier installed app build reporting that BLE advertising stopped while the user says Bluetooth is on. The app did not enter fallback mode in that screenshot. The Windows startup/error path now starts the GATT client scan when server startup fails or advertising stops; version 1.0.1.0 contains this change and still needs installation/runtime verification.
- The current INC3 iPhone source has not been built with Xcode in this Windows session. The Mac handoff lists the exact Xcode build and device checks needed next.
- The TV transport files and protocol were not changed; `TVRemote.swift` and `Discovery.swift` now branch by the Windows target role. TV behavior has not been regression-tested after those changes.

## Next steps

1. On Windows, add the development certificate in `artifacts/FreeMyTVRemote.cer` to `Cert:\LocalMachine\TrustedPeople` from an Administrator PowerShell window, then install the MSIX for the intended Windows user. Detailed commands are in `windows/FreeMyTVRemote/README.md`.
2. On Mac, build the current iOS source with Xcode, fix any compile or signing issues, then run the app on the iPhone.
3. With both devices available, verify whichever Windows role the adapter selects. Exercise first pairing, command inputs, disconnect/reconnect, and pairing revocation; also confirm the current TV path still works.
4. Record only observed results in `behaviors.md` and update this state file. Then complete INC4 review and closeout.
