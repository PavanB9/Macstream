import Foundation
import CryptoKit
import Network

/// Shared pairing state: at most one pairing alert at a time, and a short cooldown after a
/// refusal so a device on the cable cannot flood the Mac with requests. Used on the server queue.
final class PairingCoordinator {
    let store = PairingStore()
    private var busy = false
    private var cooldownUntil = Date.distantPast

    var canStartPairing: Bool { !busy && Date() >= cooldownUntil }
    func begin() { busy = true }
    func end(cooldown: TimeInterval) {
        busy = false
        cooldownUntil = Date().addingTimeInterval(cooldown)
    }
}

/// The security handshake for one new connection, before it gets any video or may send input.
/// All methods run on the FrameServer queue. See Protocol/PROTOCOL.md, "Security handshake".
final class ServerHandshake {
    enum Outcome {
        case established(SessionCipher, clientName: String)
        case failed(String)
    }

    private enum State {
        case awaitingClientHello, awaitingPairNonce, awaitingUser, done
    }

    private enum Status: UInt8 {
        case known = 0, pairingRequired = 1, refused = 2
    }

    private static let maxHandshakeBytes = 4096
    private static let helloTimeout: TimeInterval = 10
    private static let pairingTimeout: TimeInterval = 120
    private static let cooldownAfterRefusal: TimeInterval = 10

    private let conn: NWConnection
    private let queue: DispatchQueue
    private let coordinator: PairingCoordinator
    private let onFinish: (Outcome) -> Void
    private let serverEphemeral = P256.KeyAgreement.PrivateKey()

    private var state = State.awaitingClientHello
    private var buffer = Data()
    private var clientHello = Data()
    private var serverHello = Data()
    private var clientStaticKey: P256.KeyAgreement.PublicKey?
    private var clientEphemeralKey: P256.KeyAgreement.PublicKey?
    private var clientName = "PC"
    private var serverPairNonce = Data()
    private var clientPairNonce = Data()
    private var pairing = false
    private var dialog: PairingDialog?

    /// Bytes that arrived after the handshake finished (already encrypted records).
    private(set) var leftover = Data()

    init(conn: NWConnection, queue: DispatchQueue, coordinator: PairingCoordinator, onFinish: @escaping (Outcome) -> Void) {
        self.conn = conn
        self.queue = queue
        self.coordinator = coordinator
        self.onFinish = onFinish
    }

    func start() {
        queue.asyncAfter(deadline: .now() + Self.helloTimeout) { [weak self] in
            guard let self, self.state == .awaitingClientHello else { return }
            self.fail("no security handshake within \(Int(Self.helloTimeout)) s")
        }
    }

    /// Ends the handshake and closes the connection (the user forgot all paired PCs).
    func abort(_ reason: String) {
        fail(reason)
    }

    /// The connection closed or was replaced.
    func cancel() {
        guard state != .done else { return }
        state = .done
        dialog?.cancel()
        if pairing { coordinator.end(cooldown: 3) }
    }

    func receive(_ data: Data) {
        guard state != .done else { leftover.append(data); return }
        buffer.append(data)
        guard buffer.count <= Self.maxHandshakeBytes else { return fail("handshake too large") }

        while state != .done, buffer.count >= PacketHeader.size {
            guard let header = PacketHeader.decode(Data(buffer.prefix(PacketHeader.size))) else {
                return fail("not a Macstream viewer (or an old one without encryption)")
            }
            let size = PacketHeader.size + Int(header.payloadLength)
            guard size <= Self.maxHandshakeBytes else { return fail("handshake packet too large") }
            guard buffer.count >= size else { break }
            let payload = Data(buffer.subdata(in: PacketHeader.size..<size))
            buffer = Data(buffer.dropFirst(size))
            handle(header.type, payload)
        }
        if state == .done, !buffer.isEmpty {
            leftover.append(buffer)
            buffer.removeAll()
        }
    }

    private func handle(_ type: PacketType, _ payload: Data) {
        switch (state, type) {
        case (.awaitingClientHello, .authClientHello):
            handleClientHello(payload)
        case (.awaitingPairNonce, .pairNonce):
            handlePairNonce(payload)
        default:
            fail("unexpected \(type) during the handshake")
        }
    }

    // u8 version, client static key (65), client ephemeral key (65), nonce (32), u8 nameLen, name
    private func handleClientHello(_ payload: Data) {
        let k = SecureProtocol.publicKeySize, n = SecureProtocol.nonceSize
        let fixed = 1 + k + k + n + 1
        let bytes = [UInt8](payload)
        guard bytes.count >= fixed, bytes[0] == SecureProtocol.version else {
            return fail("unsupported viewer version; update the Windows app")
        }
        let nameLength = Int(bytes[fixed - 1])
        guard nameLength <= SecureProtocol.maxNameBytes, bytes.count == fixed + nameLength else {
            return fail("malformed client hello")
        }
        let staticData = payload.subdata(in: 1..<(1 + k))
        guard let staticKey = try? P256.KeyAgreement.PublicKey(x963Representation: staticData),
              let ephemeralKey = try? P256.KeyAgreement.PublicKey(x963Representation: payload.subdata(in: (1 + k)..<(1 + 2 * k))) else {
            return fail("invalid key in client hello")
        }
        clientHello = payload
        clientStaticKey = staticKey
        clientEphemeralKey = ephemeralKey
        let rawName = String(decoding: payload.subdata(in: fixed..<(fixed + nameLength)), as: UTF8.self)
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: rawName.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) })
        let cleaned = String(scalars)
        clientName = cleaned.isEmpty ? "PC" : cleaned

        if let knownName = coordinator.store.trustedName(for: staticData) {
            clientName = knownName
            sendServerHello(.known, commitment: nil)
            finish()
            return
        }

        guard coordinator.canStartPairing else {
            sendServerHello(.refused, commitment: nil)
            return fail("pairing request from \(clientName) ignored: another request is open or one was just refused")
        }
        coordinator.begin()
        pairing = true
        serverPairNonce = SecureProtocol.randomBytes(SecureProtocol.nonceSize)
        sendServerHello(.pairingRequired, commitment: SecureProtocol.commitment(
            serverStatic: coordinator.store.identity.publicKey.x963Representation,
            clientStatic: staticData,
            serverPairNonce: serverPairNonce))
        state = .awaitingPairNonce
        queue.asyncAfter(deadline: .now() + Self.pairingTimeout) { [weak self] in
            guard let self, self.state == .awaitingPairNonce || self.state == .awaitingUser else { return }
            self.dialog?.cancel()
            self.fail("pairing timed out")
        }
    }

    // u8 version, u8 status, server static key (65), server ephemeral key (65), nonce (32), [commitment (32)]
    private func sendServerHello(_ status: Status, commitment: Data?) {
        var payload = Data([SecureProtocol.version, status.rawValue])
        payload.append(coordinator.store.identity.publicKey.x963Representation)
        payload.append(serverEphemeral.publicKey.x963Representation)
        payload.append(SecureProtocol.randomBytes(SecureProtocol.nonceSize))
        if let commitment { payload.append(commitment) }
        serverHello = payload
        sendPlain(.authServerHello, payload)
    }

    private func handlePairNonce(_ payload: Data) {
        guard payload.count == SecureProtocol.nonceSize else { return fail("malformed pairing nonce") }
        clientPairNonce = payload
        sendPlain(.pairReveal, serverPairNonce)
        state = .awaitingUser

        let code = SecureProtocol.pairingCode(transcript: transcript)
        print("\(ServerText.mainTag) pairing request from “\(clientName)” — code \(code). Confirm on this Mac's screen.")
        dialog = PairingDialog.ask(deviceName: clientName, code: code, timeout: Self.pairingTimeout - 5) { [weak self] allowed in
            self?.queue.async { self?.userAnswered(allowed) }
        }
    }

    private func userAnswered(_ allowed: Bool) {
        guard state == .awaitingUser, let clientStaticKey else { return }
        pairing = false
        if allowed {
            coordinator.end(cooldown: 0)
            coordinator.store.trust(clientStaticKey.x963Representation, name: clientName)
            sendPlain(.pairResult, Data([1]))
            finish()
        } else {
            coordinator.end(cooldown: Self.cooldownAfterRefusal)
            sendPlain(.pairResult, Data([0]))
            fail("pairing with \(clientName) was not allowed")
        }
    }

    private var transcript: Data { clientHello + serverHello + clientPairNonce + serverPairNonce }

    private func finish() {
        guard let clientStaticKey, let clientEphemeralKey else { return fail("handshake state lost") }
        do {
            let identity = coordinator.store.identity
            let keys = SecureProtocol.sessionKeys(
                ss: try SecureProtocol.sharedSecret(identity, clientStaticKey),
                ee: try SecureProtocol.sharedSecret(serverEphemeral, clientEphemeralKey),
                es: try SecureProtocol.sharedSecret(identity, clientEphemeralKey),
                se: try SecureProtocol.sharedSecret(serverEphemeral, clientStaticKey),
                transcript: transcript)
            state = .done
            // The viewer sends control packets; the largest is a copied picture (12 MiB of PNG,
            // ClipboardSync.maxImageBytes). Frames go the other way.
            let cipher = SessionCipher(sendKey: keys.serverToClient, receiveKey: keys.clientToServer,
                                       maxIncomingRecord: 14 * 1024 * 1024)
            onFinish(.established(cipher, clientName: clientName))
        } catch {
            fail("key agreement failed: \(error)")
        }
    }

    private func fail(_ reason: String) {
        guard state != .done else { return }
        state = .done
        dialog?.cancel()
        if pairing {
            pairing = false
            coordinator.end(cooldown: Self.cooldownAfterRefusal)
        }
        onFinish(.failed(reason))
    }

    private func sendPlain(_ type: PacketType, _ payload: Data) {
        var packet = PacketHeader(type: type, payloadLength: UInt32(payload.count)).encode()
        packet.append(payload)
        conn.send(content: packet, completion: .contentProcessed { _ in })
    }
}
