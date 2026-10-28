# Behaviors

## INC1 — Windows receiver and secure GATT server

### INC1-BEH-1: Start the BLE service on a compatible adapter

Given a Windows PC whose Bluetooth adapter reports BLE peripheral-role support, when the receiver starts, then it advertises the Free My TV GATT service and shows that it is ready to pair.

### INC1-BEH-2: Explain an unsupported server role

Given a Windows PC whose adapter does not support the BLE peripheral role, or whose GATT service stops advertising, when the receiver starts or advertising stops, then it explains the limitation and automatically starts the iPhone-peripheral compatibility route from INC3. If Windows cannot scan or connect, it reports that state rather than claiming to be ready.

### INC1-BEH-3: Require a user-approved first pairing

Given a phone that has no stored pairing token, when it requests pairing, then the receiver accepts only the current short-lived code, issues a random token after a correct code, and rate-limits repeated incorrect codes.

### INC1-BEH-4: Reject commands without a secure paired link

Given an unencrypted BLE write or a command from an unpaired phone, when the receiver processes it, then it rejects the write and dispatches no Windows input.

### INC1-BEH-5: Dispatch only valid allowlisted frames

Given an authenticated phone, when it sends a valid versioned input frame, then the receiver maps only the documented opcode to keyboard, text, pointer, or media input. Unknown opcodes, malformed frames, unsupported versions, or oversized payloads cause no input.

### INC1-BEH-6: Protect pairing data at rest

Given a phone has paired successfully, when the receiver persists its token, then Windows stores it through DPAPI and not as plaintext configuration or log output.

## Coverage mapping

All six scenarios have source-level implementation paths in the Windows project. The x64 app compiled and packaged successfully, and Windows reports a Bluetooth adapter and a paired iPhone entry. No behavior is marked passed: installation was blocked by an untrusted development certificate, and BLE discovery, pairing, input, and reconnect have not been exercised.

| Behavior | Implementation evidence | Verification status |
|---|---|---|
| INC1-BEH-1 | `PcRemoteGattServer.StartAsync` checks peripheral-role support, creates the service, and waits for an advertising status before reporting ready. | Source inspected; Windows app compiled and packaged; adapter role and advertising not checked. |
| INC1-BEH-2 | Unsupported adapters or a stopped server advertisement switch to the GATT client path. | User reports MSIX 1.0.1.0 installed after closing 1.0.0.0; 1.0.2.0 is also installed. Fallback behavior is not yet verified. |
| INC1-BEH-3 | `PairingManager` expires the code, applies the attempt lockout, binds the credential to the GATT session device ID, and stores the token with DPAPI; the INC2 iPhone client submits the code and saves the returned token in Keychain. | Both source paths inspected; Windows app not launched and hardware pairing not checked. |
| INC1-BEH-4 | Pairing/command characteristics require encrypted access; command authentication also checks the paired device ID and HMAC token. | Source inspected; Windows enforcement and BLE behavior not checked. |
| INC1-BEH-5 | `RemoteProtocol` bounds and validates frames; `WindowsInputDispatcher` maps only known commands and releases unmatched key/button downs after partial `SendInput`. | Source inspected; Windows input behavior not checked. |
| INC1-BEH-6 | `DpapiPairingStore` protects the token and paired device ID together with current-user DPAPI. | Source inspected; DPAPI persistence not checked on Windows. |

The requirements acceptance boxes remain unchecked until end-to-end builds and device verification provide evidence. The Windows setup guide documents the current certificate-trust step; the Mac handoff lists the remaining iPhone build and device checks.

## INC2 — iPhone connects to the Windows GATT server

### INC2-BEH-1: Find a Windows receiver over Bluetooth

Given the Windows receiver is advertising its Free My TV BLE service and Bluetooth is enabled on the iPhone, when the person opens the device picker, then the receiver appears in a Windows PC section without requiring a shared Wi-Fi network.

### INC2-BEH-2: Pair the iPhone with the Windows receiver

Given an unpaired iPhone has connected to a Windows receiver, when the person enters the six-digit code shown by the receiver, then the iPhone writes the pairing request, reads the issued random token, stores it in Keychain, and reports the PC connected only after the protected status characteristic confirms the paired phone is subscribed.

### INC2-BEH-3: Reuse a saved pairing and handle missing credentials

Given the same iPhone has a saved Keychain token and the Windows receiver still recognizes its Bluetooth peer, when the iPhone reconnects, then it subscribes to status and resumes control without asking for the code again. If Windows has a saved pairing but the iPhone no longer has its token, the app explains that the pairing must be forgotten in the Windows receiver before pairing again.

### INC2-BEH-4: Route remote actions to the PC

Given the PC is the selected, connected target, when the person uses the remote, then arrows send arrow commands, touchpad movement controls the pointer, touchpad tap and hold click, and the existing back, home, menu, enter, text, volume, mute, and play/pause controls map to the documented Windows command allowlist.

### INC2-BEH-5: Keep the PC controls specific to the PC target

Given the PC is selected, when the remote is displayed, then TV app launching and TV power controls are hidden. Given a TV is selected, when the remote is displayed, then the existing TV controls, discovery, pairing, and app list continue to use the existing TCP/Bonjour behavior.

### INC2-BEH-6: Surface Bluetooth connection changes

Given the PC connection is interrupted or Bluetooth becomes unavailable, when the link fails, then the app shows a disconnected/error state and a retry action. Switching targets or moving the app to the background stops PC writes; returning to the foreground allows reconnecting.

### INC2-BEH-7: Send only authenticated, bounded protocol frames

Given the PC is connected and paired, when the app sends a command, then it increments the session sequence, authenticates the versioned frame with HMAC-SHA-256, uses acknowledged GATT writes, and splits UTF-8 text into one-to-six-byte chunks without splitting a Unicode scalar. Unpaired, disconnected, oversized, or unsupported commands are not sent as Windows input.

### INC2 coverage mapping

These behaviors were approved before implementation. Source paths are present; device-level verification remains outstanding. The current Windows build does not compile or validate the iPhone source.

| Behavior | Planned evidence | Verification status |
|---|---|---|
| INC2-BEH-1 | CoreBluetooth scans for the custom service and lists discovered peripherals in the device picker. | Source inspected and full iOS app build succeeded; Bluetooth discovery not verified on a device. |
| INC2-BEH-2 | Pairing and token read flow in the BLE transport; token persisted by the iOS Keychain store. | Source inspected, transport type-checked separately, and full app build succeeded; pairing not verified on a device. |
| INC2-BEH-3 | Saved token lookup by peripheral identifier and actionable missing-token state. | Source inspected and full app build succeeded; reconnect and Keychain behavior not verified on a device. |
| INC2-BEH-4 | Semantic remote actions mapped to the Windows command codec, including relative pointer movement. | Source inspected against the Windows protocol and full app build succeeded; input delivery not verified on Windows. |
| INC2-BEH-5 | Target-specific remote UI, with the existing TV transport preserved. | Source inspected and full app build succeeded; UI behavior not exercised on an iPhone. |
| INC2-BEH-6 | CoreBluetooth lifecycle reflected in the remote state and retry control. | Source inspected and full app build succeeded; disconnect/retry behavior not exercised on a device. |
| INC2-BEH-7 | Versioned HMAC frames, serialized acknowledged writes, and Unicode-safe bounded text chunks. | Source inspected, transport type-checked separately, and full app build succeeded; cross-platform frame exchange not verified. |

The full Xcode app build succeeded when Xcode was allowed to launch its compiler helpers outside the restricted sandbox; the sandboxed build had failed while starting those helpers. The standalone BLE transport, Keychain store, and shared key declarations also type-checked against the iOS SDK. No tests were added or run.

## INC3 — iPhone peripheral / Windows client fallback

**Approval:** The user approved this behavior proposal on 2026-10-29. Source implementation is in progress. These are acceptance criteria, not device-test results.

- **INC3-BEH-1 — Select the Windows BLE role.** At startup, Windows uses its GATT server when `IsPeripheralRoleSupported` is true. Otherwise it scans as a GATT client for the iPhone service and reports fallback mode. Adapter or scan errors are surfaced in the Windows window.
- **INC3-BEH-2 — Advertise from the foreground iPhone app.** Opening the Windows connection screen starts the matching iPhone GATT service and advertisement. The screen remains foregrounded while pairing and control are in use. Backgrounding stops the BLE connection; the Windows fallback remote has an explicit stop control.
- **INC3-BEH-3 — Pair in the reversed role direction.** The iPhone creates and displays a six-digit code for five minutes. The person enters it in Windows, which writes it to the iPhone pairing characteristic. The iPhone applies three-attempt/one-minute lockout, returns a random 32-byte token after a correct code, and stores it in Keychain; Windows stores the token and peer identity with DPAPI. Pairing reads and writes require an encrypted BLE link.
- **INC3-BEH-4 — Deliver authenticated commands from the iPhone peripheral.** Once Windows subscribes to the command characteristic, the iPhone sends the existing versioned, bounded, HMAC-authenticated frames as BLE notifications. Windows checks peer identity, authentication tag, opcode, payload, and increasing sequence before dispatching input. The iPhone queues notifications when CoreBluetooth reports backpressure. No command is sent before pairing completes.
- **INC3-BEH-5 — Reuse pairing and report lifecycle.** A returning Windows receiver reconnects without a code only when its saved token and peer identity match. The iPhone shows connection state and allows stopping the link or revoking its pairing. Windows exposes its active role, scan state, pairing entry, and reconnect behavior.
- **INC3-BEH-6 — Preserve existing target behavior.** TV discovery, pairing, controls, TCP/Bonjour transport, TV app launching, and power behavior remain unchanged. PC mode continues to hide TV-only controls.

| Behavior | Implementation evidence | Verification status |
|---|---|---|
| INC3-BEH-1 | `MainWindow` starts the Windows GATT client when the server role is unsupported, startup fails, or server advertising stops; the client filters advertisements by the shared service UUID and restarts scanning after disconnect. | Windows source compiled and MSIX 1.0.2.0 was installed. Runtime fallback, role detection, and scanning remain unverified. |
| INC3-BEH-2 | The iPhone starts CoreBluetooth peripheral advertising with the Windows picker and exposes a stop control; scene backgrounding disconnects. | User reports the iPhone app was built, signed, and installed from commit `3002333`. Advertising and foreground/background behavior have not been verified. |
| INC3-BEH-3 | The iPhone peripheral code uses a five-minute code, three-attempt lockout, encrypted GATT permissions, and Keychain token storage; Windows uses the existing DPAPI pairing manager and requests encrypted access for protected pairing/status characteristics. | The iPhone app was installed, but the user reported the code screen disappearing and pairing not resolving the disconnect. The Windows error shown was clipped after “Cannot acc…”. Updated Windows source compiled with 0 errors and 3 existing CA1416 warnings; MSIX 1.0.2.0 was installed. Pairing and encryption negotiation have not yet been retried. |
| INC3-BEH-4 | The iPhone uses the shared HMAC frame codec and notification backpressure callback; Windows authenticates notifications through the shared protocol and allowlisted dispatcher. | Windows build succeeded. iPhone build and command delivery are unverified. |
| INC3-BEH-5 | Both clients retain their peer identifiers and protected tokens; disconnect handling and retry UI are present. | Source inspected. Reconnect, revoke, and role UI are unverified on devices. |
| INC3-BEH-6 | The new fallback target is handled in the existing Windows transport facade; the TV networking and command paths were not edited. | Source diff inspected; no TV regression run or iPhone build in this Windows session. |

No INC3 behavior is marked passed. MSIX 1.0.2.0 is installed for the current Windows user. The reported disconnect has not yet been retested with the encrypted-access fix. Pairing and command behavior still need checking with the iPhone and Windows PC before closeout.
