# Requirements: control a Windows PC from the iPhone remote over Bluetooth

## Problem and scope

The user wants to use the existing Free My TV iPhone companion app to control a Windows PC over Bluetooth when the phone and PC are not connected to the same Wi-Fi network. This adds Windows as a second device type; existing TV control must continue to work.

The feature includes a small Windows receiver app, BLE discovery and connection in the iPhone app, secure pairing, and common remote input. The receiver acts only while running and within Bluetooth range. It does not provide remote desktop video or control over the internet.

## Acceptance criteria

- [ ] The iPhone and Windows receiver can connect over BLE in either role direction, without shared Wi-Fi or internet: Windows advertises when its adapter supports the BLE peripheral role; otherwise Windows scans for the iPhone's advertised service.
- [ ] The receiver reports which connection mode it is using and clearly explains when Windows Bluetooth or its driver cannot provide either required BLE role.
- [ ] A first-time connection requires the user to approve a pairing step; later control is limited to a paired phone. The pairing secret is stored using platform-protected storage, and failed pairing attempts are rate-limited.
- [ ] The existing remote UI can control Windows with a touch surface or arrows, select/enter, back/escape, home, volume, mute, play/pause, and text entry. The touch surface can move and click the Windows pointer.
- [ ] The Windows receiver accepts only documented remote commands; it does not execute arbitrary shell commands or expose a general-purpose input injection interface.
- [ ] The app clearly identifies which target is selected and when it is disconnected or out of Bluetooth range.
- [ ] Existing TV discovery, TV pairing, and TV controls continue to work unchanged; TV-specific app launching remains available only for TVs.
- [ ] User-facing setup instructions explain how to start the Windows receiver, connect from the iPhone app, and the Bluetooth adapter/range requirements.

## Assumptions

- Initial support targets Windows 10 version 1703 or later, including Windows 11, subject to BLE peripheral-role support in the PC adapter and driver.
- The receiver package targets x64 and ARM64 Windows PCs; the project compiles against Windows 10 SDK 2004 while declaring Windows 10 version 1703 as its supported minimum.
- BLE is the transport; classic Bluetooth/RFCOMM is out of scope.
- The iPhone remote remains foregrounded for the first version; background reconnect/discovery is out of scope.
- The user approved supporting both BLE role directions on 2026-10-28: prefer Windows as GATT server when supported, and fall back to Windows as GATT client with the iPhone advertising.
- Windows apps running elevated may reject simulated input from a normal receiver process; the receiver will not request administrator privileges by default.

## Open requirements questions

None identified for an MVP. The exact pairing UX and Windows packaging format are design choices for the next phase.
