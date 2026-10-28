# Implementation increments

**N = 4.** The work crosses a Windows receiver and an existing iPhone app. The slices are ordered so a Windows server path can be built first, the iPhone can use it next, and the lower-compatibility fallback can be added after the shared protocol is proven in code.

## INC1 — Windows receiver and secure GATT server

Build the Windows receiver shell, custom GATT service, encrypted command characteristic, pairing flow, protected token storage, and allowlisted input dispatcher. Detect whether the PC adapter supports BLE peripheral mode. If it does not, explain the limitation and start the iPhone fallback path from INC3.

Acceptance criteria:

- A Windows 10/11 receiver can advertise the custom GATT service when `IsPeripheralRoleSupported` is true.
- The receiver displays its pairing code and connection state, rate-limits failed attempts, and stores the paired-phone token using DPAPI.
- It accepts only the versioned input commands and maps them to keyboard, text, media, and pointer input; it never executes shell commands.
- Unsupported adapters receive a clear explanation instead of a failed or silent connection.

## INC2 — iPhone connects to Windows GATT server

Add CoreBluetooth central discovery and connection to the iOS app, the first-pairing flow and Keychain token storage, and route the existing remote actions through the BLE command codec for a PC target. Preserve the current TCP TV transport.

Acceptance criteria:

- A compatible Windows receiver appears in a Bluetooth PC section in the iPhone app and can be paired using the Windows code.
- D-pad, pointer touchpad, click/hold, keyboard text, volume, mute, and play/pause control the paired PC.
- Connection state, disconnects, retry, and target switching are visible in the app.
- Existing TV discovery, pairing, and controls remain available through the existing Wi-Fi path.

## INC3 — iPhone peripheral / Windows client fallback

Add the reverse GATT role path. The iPhone advertises its remote service while the PC connection screen is active; Windows scans and connects when its adapter cannot act as a peripheral. Reuse the protocol, pairing rules, and input dispatcher from INC1/2.

Acceptance criteria:

- A Windows receiver whose adapter lacks BLE peripheral support can find the foregrounded iPhone remote, pair, and control the PC.
- The iPhone reports the active PC connection and provides a way to stop advertising or revoke the pairing.
- Both role directions enforce encrypted GATT writes, pairing expiry/rate limits, and token authorization.

## INC4 — Integrated UI, setup guide, and closeout

Finish target-specific UI behavior, clarify the two connection paths, document Windows setup and adapter requirements, review all changes, and complete available integration builds and device checks.

Acceptance criteria:

- PC mode hides TV app launching and the TV power button; TV mode keeps both.
- The Windows receiver and iPhone app explain which Bluetooth role is active and what to do if pairing or discovery fails.
- README instructions cover installing/running the receiver, connecting from the iPhone, Bluetooth range, required OS versions, and the elevated-app limitation.
- Review confirms TV protocol behavior was not changed and all requirements are mapped to implementation and available verification evidence.

## Ordering and dependencies

1. INC1 defines the on-air protocol and Windows GATT server; INC2 consumes that protocol.
2. INC3 adds the alternate roles using the same protocol, so its work depends on INC1 and INC2.
3. INC4 depends on both connection paths and closes the cross-platform setup and UI experience.
