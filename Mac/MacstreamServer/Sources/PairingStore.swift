import Foundation
import CryptoKit
import Security

/// The Mac's pairing keys, kept in the user's login Keychain (not in a plain file):
/// - "identity": this Mac's long-term P-256 private key.
/// - "trusted-clients": the public keys (and names) of the PCs the user allowed.
///
/// Forget all paired PCs: menu bar "Forget Paired PCs…" or ./tools/mac/unpair.sh (the
/// uninstaller removes both items).
/// Used on the FrameServer queue only (the menu bar goes through FrameServer).
final class PairingStore {
    static let service = "com.macstream.server"
    private static let identityAccount = "identity"
    private static let trustedAccount = "trusted-clients"
    private static let maxTrustedClients = 16

    struct TrustedClient: Codable {
        let key: Data       // X9.63 public key
        let name: String
        let added: Date
    }

    let identity: P256.KeyAgreement.PrivateKey
    private var trusted: [TrustedClient]

    init() {
        if let raw = Self.load(Self.identityAccount),
           let key = try? P256.KeyAgreement.PrivateKey(rawRepresentation: raw) {
            identity = key
        } else {
            let key = P256.KeyAgreement.PrivateKey()
            identity = key
            if Self.save(Self.identityAccount, key.rawRepresentation) {
                print("\(ServerText.mainTag) created this Mac's pairing key (stored in the login Keychain)")
            } else {
                print("\(ServerText.mainTag) WARNING: could not store the pairing key in the Keychain; PCs will have to pair again after a restart")
            }
        }

        if let data = Self.load(Self.trustedAccount),
           let list = try? JSONDecoder().decode([TrustedClient].self, from: data) {
            trusted = list
        } else {
            trusted = []
        }
        print("\(ServerText.mainTag) paired PCs: \(trusted.isEmpty ? "none yet" : trusted.map(\.name).joined(separator: ", "))")
    }

    /// Names of the paired PCs, oldest first (menu bar).
    var trustedNames: [String] { trusted.map(\.name) }

    /// Short fingerprint of this Mac's key ("AB12 CD34"); the Windows Settings window shows the
    /// same for the Mac it is paired with.
    var identityFingerprint: String { Self.fingerprint(identity.publicKey.x963Representation) }

    /// First 4 bytes of SHA-256 of an X9.63 public key, as "AB12 CD34" (display only).
    static func fingerprint(_ key: Data) -> String {
        let hex = SHA256.hash(data: key).prefix(4).map { String(format: "%02X", $0) }.joined()
        return "\(hex.prefix(4)) \(hex.suffix(4))"
    }

    /// Forgets every paired PC (menu bar, like tools/mac/unpair.sh). This Mac's key is kept.
    func forgetAll() {
        trusted = []
        let status = SecItemDelete(Self.baseQuery(Self.trustedAccount) as CFDictionary)
        if status == errSecSuccess || status == errSecItemNotFound {
            print("\(ServerText.mainTag) forgot all paired PCs (menu bar); they must pair again")
        } else {
            print("\(ServerText.mainTag) WARNING: could not delete the paired PCs from the Keychain (\(status)); they are forgotten until the server restarts")
        }
    }

    func trustedName(for clientKey: Data) -> String? {
        trusted.first { $0.key == clientKey }?.name
    }

    func trust(_ clientKey: Data, name: String) {
        trusted.removeAll { $0.key == clientKey }
        trusted.append(TrustedClient(key: clientKey, name: name, added: Date()))
        if trusted.count > Self.maxTrustedClients {
            trusted.removeFirst(trusted.count - Self.maxTrustedClients)
        }
        if let data = try? JSONEncoder().encode(trusted), Self.save(Self.trustedAccount, data) {
            print("\(ServerText.mainTag) paired with \(name)")
        } else {
            print("\(ServerText.mainTag) WARNING: could not store the paired PC in the Keychain; it will have to pair again after a restart")
        }
    }

    // MARK: - Keychain (generic password items for this service)

    private static func baseQuery(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    private static func load(_ account: String) -> Data? {
        var query = baseQuery(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status != errSecSuccess && status != errSecItemNotFound {
            print("\(ServerText.mainTag) Keychain read failed for \(account): \(status)")
        }
        return status == errSecSuccess ? result as? Data : nil
    }

    private static func save(_ account: String, _ data: Data) -> Bool {
        let query = baseQuery(account)
        var status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrLabel as String] = "Macstream pairing (\(account))"
            status = SecItemAdd(add as CFDictionary, nil)
        }
        if status != errSecSuccess {
            print("\(ServerText.mainTag) Keychain write failed for \(account): \(status)")
        }
        return status == errSecSuccess
    }
}

/// The macOS alert that asks whether a new PC may pair. "Don't Allow" is the default button,
/// so pressing Return or ignoring it never grants access.
final class PairingDialog {
    private var notification: CFUserNotification?

    /// Shows the alert; `completion(true)` only if the user clicked Allow. Called on a
    /// background queue.
    static func ask(deviceName: String, code: String, timeout: TimeInterval, completion: @escaping (Bool) -> Void) -> PairingDialog {
        let dialog = PairingDialog()
        let info: [String: Any] = [
            kCFUserNotificationAlertHeaderKey as String: "Allow “\(deviceName)” to see and control this Mac?",
            kCFUserNotificationAlertMessageKey as String:
                "Code on the PC: \(code)\n\nClick Allow only if the Windows PC shows exactly this code. "
                + "It will see this screen and use the mouse and keyboard over the cable.",
            kCFUserNotificationDefaultButtonTitleKey as String: "Don’t Allow",
            kCFUserNotificationAlternateButtonTitleKey as String: "Allow",
        ]
        var error: Int32 = 0
        guard let note = CFUserNotificationCreate(kCFAllocatorDefault, timeout,
                                                  CFOptionFlags(kCFUserNotificationCautionAlertLevel),
                                                  &error, info as CFDictionary) else {
            print("\(ServerText.mainTag) could not show the pairing alert (error \(error)); pairing refused")
            DispatchQueue.global().async { completion(false) }
            return dialog
        }
        dialog.notification = note
        DispatchQueue.global(qos: .userInitiated).async {
            var flags: CFOptionFlags = 0
            let result = CFUserNotificationReceiveResponse(note, timeout, &flags)
            let allowed = result == 0 && (flags & 0x3) == CFOptionFlags(kCFUserNotificationAlternateResponse)
            completion(allowed)
        }
        return dialog
    }

    /// Closes the alert (e.g. the PC disconnected); counts as Don't Allow.
    func cancel() {
        if let notification { CFUserNotificationCancel(notification) }
    }
}
