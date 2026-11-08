import Foundation
import CryptoKit

/// Macstream's pairing and encryption (see Protocol/PROTOCOL.md, "Security handshake").
///
/// - Each side has a long-term P-256 key. Pairing exchanges them once, confirmed by the user:
///   both screens show the same 6-digit code, and the Mac asks before trusting a new PC.
///   The Mac commits to its nonce before seeing the PC's, so a device in the middle of the
///   cable cannot pick values that make the two codes match (1 in a million per attempt).
/// - Every connection then runs an ephemeral + static ECDH handshake (forward secret; only the
///   paired PC and Mac can derive the keys) and all packets are sealed with AES-256-GCM.
enum SecureProtocol {
    static let version: UInt8 = 1
    static let publicKeySize = 65          // X9.63 uncompressed P-256 point
    static let nonceSize = 32
    static let maxNameBytes = 64

    static func sha256(_ parts: Data...) -> Data {
        var hash = SHA256()
        for part in parts { hash.update(data: part) }
        return Data(hash.finalize())
    }

    static func label(_ text: String) -> Data { Data(text.utf8) }

    static func randomBytes(_ count: Int) -> Data {
        SymmetricKey(size: SymmetricKeySize(bitCount: count * 8)).withUnsafeBytes { Data($0) }
    }

    /// The Mac's commitment to its pairing nonce, sent before it sees the PC's nonce.
    static func commitment(serverStatic: Data, clientStatic: Data, serverPairNonce: Data) -> Data {
        sha256(label("macstream commit v1"), serverStatic, clientStatic, serverPairNonce)
    }

    /// The 6-digit code both screens show, e.g. "042 917".
    static func pairingCode(transcript: Data) -> String {
        let h = [UInt8](sha256(label("macstream pairing code v1"), transcript))
        let value = (UInt32(h[0]) << 24 | UInt32(h[1]) << 16 | UInt32(h[2]) << 8 | UInt32(h[3])) % 1_000_000
        return String(format: "%03d %03d", value / 1000, value % 1000)
    }

    static func sharedSecret(_ privateKey: P256.KeyAgreement.PrivateKey, _ publicKey: P256.KeyAgreement.PublicKey) throws -> Data {
        try privateKey.sharedSecretFromKeyAgreement(with: publicKey).withUnsafeBytes { Data($0) }
    }

    /// Session keys from the four ECDH results (static-static, ephemeral-ephemeral and both
    /// mixed pairs) and the handshake transcript.
    static func sessionKeys(ss: Data, ee: Data, es: Data, se: Data, transcript: Data) -> (clientToServer: SymmetricKey, serverToClient: SymmetricKey) {
        let salt = sha256(label("macstream session v1"), transcript)
        let okm = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: ss + ee + es + se),
            salt: salt,
            info: label("macstream v1 keys"),
            outputByteCount: 64)
        let bytes = okm.withUnsafeBytes { Data($0) }
        return (SymmetricKey(data: bytes.subdata(in: 0..<32)), SymmetricKey(data: bytes.subdata(in: 32..<64)))
    }
}

enum SecureChannelError: Error {
    case recordTooLarge(Int)
    case counterExhausted
}

/// AES-256-GCM record layer for one connection, after the handshake.
///
/// Record: u32 LE plaintext length | ciphertext | 16-byte tag. The length is authenticated as
/// associated data. Nonces are a per-direction counter (4 zero bytes + u64 LE), never sent and
/// never reused, because each direction has its own key. Any tampering, reordering, replay or
/// dropped record fails authentication and the connection is closed.
final class SessionCipher {
    static let tagSize = 16

    private let sendKey: SymmetricKey
    private let receiveKey: SymmetricKey
    private let maxIncomingRecord: Int
    private var sendCounter: UInt64 = 0
    private var receiveCounter: UInt64 = 0
    private var inbox = Data()

    init(sendKey: SymmetricKey, receiveKey: SymmetricKey, maxIncomingRecord: Int) {
        self.sendKey = sendKey
        self.receiveKey = receiveKey
        self.maxIncomingRecord = maxIncomingRecord
    }

    private static func nonce(_ counter: UInt64) throws -> AES.GCM.Nonce {
        var bytes = [UInt8](repeating: 0, count: 12)
        var c = counter.littleEndian
        withUnsafeBytes(of: &c) { raw in
            for i in 0..<8 { bytes[4 + i] = raw[i] }
        }
        return try AES.GCM.Nonce(data: bytes)
    }

    private static func lengthBytes(_ length: Int) -> Data {
        var l = UInt32(length).littleEndian
        return Data(bytes: &l, count: 4)
    }

    /// Encrypts one packet (header + payload) into one record.
    func seal(_ plaintext: Data) throws -> Data {
        guard sendCounter < UInt64.max else { throw SecureChannelError.counterExhausted }
        let length = Self.lengthBytes(plaintext.count)
        let box = try AES.GCM.seal(plaintext, using: sendKey, nonce: Self.nonce(sendCounter), authenticating: length)
        sendCounter += 1
        var record = Data(capacity: 4 + plaintext.count + Self.tagSize)
        record.append(length)
        record.append(box.ciphertext)
        record.append(box.tag)
        return record
    }

    /// Adds received bytes and returns every complete record, decrypted. Throws on any
    /// authentication failure.
    func open(_ data: Data) throws -> [Data] {
        inbox.append(data)
        var plaintexts: [Data] = []
        while inbox.count >= 4 {
            let b = [UInt8](inbox.prefix(4))
            let length = Int(UInt32(b[0]) | UInt32(b[1]) << 8 | UInt32(b[2]) << 16 | UInt32(b[3]) << 24)
            guard length <= maxIncomingRecord else { throw SecureChannelError.recordTooLarge(length) }
            let total = 4 + length + Self.tagSize
            guard inbox.count >= total else { break }

            let record = Data(inbox.prefix(total))   // zero-based copy
            inbox = Data(inbox.dropFirst(total))
            let box = try AES.GCM.SealedBox(
                nonce: Self.nonce(receiveCounter),
                ciphertext: record.subdata(in: 4..<(4 + length)),
                tag: record.subdata(in: (4 + length)..<total))
            plaintexts.append(try AES.GCM.open(box, using: receiveKey, authenticating: record.subdata(in: 0..<4)))
            receiveCounter += 1
        }
        return plaintexts
    }
}
