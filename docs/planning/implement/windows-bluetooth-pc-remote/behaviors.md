# Behaviors

## INC1 — Windows receiver and secure GATT server

### INC1-BEH-1: Start the BLE service on a compatible adapter

Given a Windows PC whose Bluetooth adapter reports BLE peripheral-role support, when the receiver starts, then it advertises the Free My TV GATT service and shows that it is ready to pair.

### INC1-BEH-2: Explain an unsupported server role

Given a Windows PC whose adapter does not support the BLE peripheral role, when the receiver starts, then it explains that this connection direction is unavailable and does not claim to be ready. The iPhone-peripheral compatibility route is added in INC3.

### INC1-BEH-3: Require a user-approved first pairing

Given a phone that has no stored pairing token, when it requests pairing, then the receiver accepts only the current short-lived code, issues a random token after a correct code, and rate-limits repeated incorrect codes.

### INC1-BEH-4: Reject commands without a secure paired link

Given an unencrypted BLE write or a command from an unpaired phone, when the receiver processes it, then it rejects the write and dispatches no Windows input.

### INC1-BEH-5: Dispatch only valid allowlisted frames

Given an authenticated phone, when it sends a valid versioned input frame, then the receiver maps only the documented opcode to keyboard, text, pointer, or media input. Unknown opcodes, malformed frames, unsupported versions, or oversized payloads cause no input.

### INC1-BEH-6: Protect pairing data at rest

Given a phone has paired successfully, when the receiver persists its token, then Windows stores it through DPAPI and not as plaintext configuration or log output.

## Coverage mapping

All six scenarios have source-level implementation paths in the Windows project. None is marked passed because this workspace has no Windows/.NET SDK or BLE hardware, and the session instruction forbids adding or running tests unless the user requests verification.

| Behavior | Implementation evidence | Verification status |
|---|---|---|
| INC1-BEH-1 | `PcRemoteGattServer.StartAsync` checks peripheral-role support, creates the service, and waits for an advertising status before reporting ready. | Source inspected; Windows build and adapter check not run. |
| INC1-BEH-2 | Unsupported adapters produce a specific explanation and do not start advertising. | Source inspected; adapter behavior not run. |
| INC1-BEH-3 | `PairingManager` expires the code, applies the attempt lockout, binds the credential to the GATT session device ID, and stores the token with DPAPI; the INC2 iPhone client submits the code and saves the returned token in Keychain. | Both source paths inspected; hardware pairing is not run. |
| INC1-BEH-4 | Pairing/command characteristics require encrypted access; command authentication also checks the paired device ID and HMAC token. | Source inspected; Windows enforcement and BLE behavior not run. |
| INC1-BEH-5 | `RemoteProtocol` bounds and validates frames; `WindowsInputDispatcher` maps only known commands and releases unmatched key/button downs after partial `SendInput`. | Source inspected; Windows input behavior not run. |
| INC1-BEH-6 | `DpapiPairingStore` protects the token and paired device ID together with current-user DPAPI. | Source inspected; DPAPI persistence not run on Windows. |

The requirements acceptance boxes remain unchecked until end-to-end builds and device verification provide evidence. README setup now identifies the iPhone flow as a later increment rather than a usable feature today.

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

These behaviors were approved before implementation. Source paths are now present; device-level verification remains outstanding. The Windows client fallback role remains INC3.

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
