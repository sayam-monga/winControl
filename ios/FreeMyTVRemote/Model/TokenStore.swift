import Foundation
import Security

/// Pairing credentials for TVs and Windows PCs, in the device-only Keychain.
enum TokenStore {
    private static let service = "io.freemytv.remote.token"

    static func token(for tvID: String) -> String? {
        guard let data = data(for: tvID) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func data(for deviceID: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: deviceID,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return data
    }

    @discardableResult
    static func save(_ token: String, for tvID: String) -> Bool {
        save(Data(token.utf8), for: tvID)
    }

    @discardableResult
    static func save(_ token: Data, for deviceID: String) -> Bool {
        forget(deviceID)
        let item: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: deviceID,
            kSecValueData as String: token,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    static func forget(_ deviceID: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: deviceID,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
