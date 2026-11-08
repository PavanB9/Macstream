import Foundation
import Network

struct ClientDisplayInfo {
    let width: Int
    let height: Int
    let refreshRate: Int
    let dpi: Int
    /// What the viewer asked to see (HELLO bytes 16-31); nil from viewers that don't send it.
    var screen: ScreenRequest? = nil
}

/// The viewer's choice in its Settings window ("Mac screen").
struct ScreenRequest: Equatable {
    /// false = mirror the Mac's own screen (the default), true = an extra Mac display.
    let extraDisplay: Bool
    /// Extra display text size: 0 = automatic, 1 = normal (1:1 pixels), 2 = Retina (2x).
    let textScale: Int
    /// Extra display size in pixels; 0 = the PC monitor's size.
    let width: Int
    let height: Int
}

class FrameServer {
    private static let queueKey = DispatchSpecificKey<Void>()
    private static let maxControlPayloadLength = 13 * 1024 * 1024   // a copied picture is the largest

    private enum PacketKind {
        case frame
        case realtime
    }

    private var listener: NWListener?
    private var connections: [NWConnection] = []
    private var lastFramePacket: Data?
    private var sendingConnectionIds: Set<ObjectIdentifier> = []
    private var sendStartedAt: [ObjectIdentifier: Date] = [:]
    private var pendingFramePackets: [ObjectIdentifier: Data] = [:]
    private var pendingRealtimePackets: [ObjectIdentifier: Data] = [:]
    /// A CLIPBOARD packet waiting for the connection; a newer copy replaces an unsent one.
    private var pendingClipboardPackets: [ObjectIdentifier: Data] = [:]
    /// INPUT_FEEDBACK packets waiting behind a send in flight. All of them go, in order (each
    /// one matters, unlike stats), before anything else that is waiting.
    private var pendingFeedbackPackets: [ObjectIdentifier: [Data]] = [:]
    private var receiveBuffers: [ObjectIdentifier: Data] = [:]
    private var lastClientDisplayInfo: ClientDisplayInfo?
    /// Authenticated connections that also sent HELLO; input is only accepted from these.
    private var helloConnectionIds: Set<ObjectIdentifier> = []
    /// Connections still doing the security handshake: they get no video and no input.
    private var handshakes: [ObjectIdentifier: ServerHandshake] = [:]
    /// Encryption for each authenticated connection (everything in `connections` has one).
    private var sessions: [ObjectIdentifier: SessionCipher] = [:]
    private lazy var pairing = PairingCoordinator()
    private static let maxPendingHandshakes = 4
    private var firstClientNotificationSent = false
    private var droppedPendingFrames: UInt64 = 0
    private var droppedForStats: UInt64 = 0
    private var lastDropReportTime = Date()

    /// Frames dropped by the send queue since the last call (for the per-second STATS packet).
    func takeDroppedFrameCount() -> UInt64 {
        syncOnQueue {
            let count = droppedForStats
            droppedForStats = 0
            return count
        }
    }
    /// Forgets the cached keyframe (used when the streamed display changes, so a new viewer
    /// doesn't first get a picture of the old one).
    func clearCachedFrame() {
        syncOnQueue { lastFramePacket = nil }
    }

    private let port: UInt16
    private let queue = DispatchQueue(label: "com.macstream.server", qos: .userInteractive)

    var clientCount: Int {
        syncOnQueue {
            connections.count
        }
    }
    var isFrameBackpressured: Bool {
        syncOnQueue {
            !sendingConnectionIds.isEmpty || !pendingFramePackets.isEmpty
        }
    }

    /// True only when a frame is already queued behind the one being sent, meaning the link is
    /// genuinely behind. A frame merely in flight is normal (it takes ~1-2 ms at 1 Gbps) and the
    /// encoder should keep working on the next one meanwhile, rather than waiting.
    var hasFrameBacklog: Bool {
        syncOnQueue {
            !pendingFramePackets.isEmpty
        }
    }
    var onFirstClientConnected: ((ClientDisplayInfo?) -> Void)?
    var onLastClientDisconnected: (() -> Void)?
    /// INPUT_EVENT payloads from the viewer (Windows -> Mac control), delivered on the server queue.
    var onInputEvent: ((Data) -> Void)?
    /// CLIPBOARD payloads from the viewer, delivered on the server queue.
    var onClipboard: ((Data) -> Void)?

    init(port: UInt16) {
        self.port = port
        queue.setSpecific(key: Self.queueKey, value: ())
    }

    func start() -> Bool {
        _ = pairing   // load the pairing keys now, so Keychain problems show up at startup
        let tcpOptions = NWProtocolTCP.Options()
        tcpOptions.noDelay = true
        let params = NWParameters(tls: nil, tcp: tcpOptions)
        applyInterfaceRestriction(to: params)

        do {
            listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
        } catch {
            print("\(ServerText.serverTag) failed to create listener: \(error)")
            return false
        }

        listener?.stateUpdateHandler = { state in
            if case .ready = state {
                print("\(ServerText.serverTag) TCP listening on port \(self.port)")
            }
        }

        listener?.newConnectionHandler = { [weak self] conn in
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let self else { conn.cancel(); return }
                    if !self.connections.isEmpty, !self.allowMultipleClients {
                        print("\(ServerText.serverTag) a client is already connected; rejecting new connection: \(conn.endpoint)")
                        conn.cancel()
                        return
                    }
                    guard self.handshakes.count < Self.maxPendingHandshakes else {
                        print("\(ServerText.serverTag) too many connections still in the security handshake; rejecting \(conn.endpoint)")
                        conn.cancel()
                        return
                    }
                    // Nothing is sent to (or accepted from) this connection until it has passed
                    // the security handshake; see promote().
                    let id = ObjectIdentifier(conn)
                    let handshake = ServerHandshake(conn: conn, queue: self.queue, coordinator: self.pairing) { [weak self, weak conn] outcome in
                        guard let self, let conn else { return }
                        self.handshakes.removeValue(forKey: id)
                        switch outcome {
                        case .established(let cipher, let clientName):
                            self.promote(conn, cipher: cipher, clientName: clientName)
                        case .failed(let reason):
                            print("\(ServerText.serverTag) connection from \(conn.endpoint) refused: \(reason)")
                            // Give the last handshake message a moment to leave, then close.
                            self.queue.asyncAfter(deadline: .now() + 0.3) { conn.cancel() }
                        }
                    }
                    self.handshakes[id] = handshake
                    handshake.start()
                    self.receiveControlPackets(from: conn)
                case .failed, .cancelled:
                    self?.removeConnection(conn)
                default:
                    break
                }
            }
            conn.start(queue: self?.queue ?? .main)
        }

        listener?.start(queue: queue)
        return true
    }

    /// Security default: only one viewer at a time. Opt in to more with MACSTREAM_ALLOW_MULTI_CLIENT=1.
    private var allowMultipleClients: Bool {
        environmentValue("MACSTREAM_ALLOW_MULTI_CLIENT") == "1"
    }

    /// Security default: listen on wired Ethernet only (USB-C/Thunderbolt Ethernet adapters),
    /// so the Mac is never reachable over Wi-Fi (on top of pairing and encryption).
    ///
    /// MACSTREAM_INTERFACE:
    ///   unset / "wired" -> wired Ethernet only (default)
    ///   "other"          -> interfaces Network.framework classifies as "other" (e.g. bridges); for Thunderbolt Bridge testing
    ///   "any"            -> every interface, including Wi-Fi (explicit opt-in, prints a warning)
    private func applyInterfaceRestriction(to params: NWParameters) {
        let mode = environmentValue("MACSTREAM_INTERFACE")?.lowercased() ?? "wired"
        switch mode {
        case "any":
            print("\(ServerText.serverTag) WARNING: listening on ALL interfaces including Wi-Fi (connections are still paired and encrypted)")
        case "other":
            params.requiredInterfaceType = .other
            params.prohibitedInterfaceTypes = [.wifi, .cellular]
            print("\(ServerText.serverTag) listening on 'other' interfaces only (e.g. Thunderbolt Bridge)")
        default:
            params.requiredInterfaceType = .wiredEthernet
            params.prohibitedInterfaceTypes = [.wifi, .cellular]
            print("\(ServerText.serverTag) listening on wired Ethernet interfaces only")
        }
    }

    /// A connection passed the security handshake: from now on everything is encrypted, and it
    /// becomes a viewer (video, input), exactly as unencrypted connections used to on connect.
    private func promote(_ conn: NWConnection, cipher: SessionCipher, clientName: String) {
        let id = ObjectIdentifier(conn)
        if !connections.isEmpty && !allowMultipleClients {
            print("\(ServerText.serverTag) a client is already connected; rejecting new connection: \(conn.endpoint)")
            conn.cancel()
            return
        }
        sessions[id] = cipher
        print("\(ServerText.serverTag) client connected: \(clientName) (\(conn.endpoint)), paired and encrypted")
        let wasEmpty = connections.isEmpty
        connections.append(conn)
        // The cached keyframe gives an instant (possibly slightly old) picture. The
        // frames encoded since then reference pictures this viewer never received,
        // so hold back predicted frames until a fresh keyframe, and ask for one now.
        if let packet = lastFramePacket {
            sendPacket(packet, to: conn, kind: .frame)
        }
        awaitingKeyFrame.insert(id)
        requestKeyFrame()
        if wasEmpty {
            notifyFirstClientAfterHelloTimeout()
        }
    }

    func stop() {
        listener?.cancel()
        handshakes.values.forEach { $0.cancel() }
        handshakes.removeAll()
        sessions.removeAll()
        connections.forEach { $0.cancel() }
        connections.removeAll()
        sendingConnectionIds.removeAll()
        pendingFramePackets.removeAll()
        pendingRealtimePackets.removeAll()
        pendingClipboardPackets.removeAll()
        pendingFeedbackPackets.removeAll()
        receiveBuffers.removeAll()
        awaitingKeyFrame.removeAll()
        print("\(ServerText.serverTag) stopped")
    }

    /// Sends one encoded frame to every viewer.
    ///
    /// H.264/HEVC predicted frames only decode correctly if every earlier frame since the last
    /// keyframe arrived. So a predicted frame is never silently replaced or skipped: if a viewer
    /// can't keep up, it stops receiving predicted frames until the next keyframe, and the
    /// encoder is asked for one immediately (onKeyFrameNeeded).
    func sendFrame(data: Data, packetType: PacketType, isKeyFrame: Bool, cacheForNewClients: Bool = true) {
        var packet = Data(capacity: 10 + data.count)
        let header = PacketHeader(type: packetType, payloadLength: UInt32(data.count))
        packet.append(header.encode())
        packet.append(data)

        syncOnQueue {
            if cacheForNewClients && isKeyFrame {
                self.lastFramePacket = packet
            }
            guard !self.connections.isEmpty else { return }

            var keyFrameNeeded = false
            for conn in self.connections {
                let id = ObjectIdentifier(conn)

                if self.awaitingKeyFrame.contains(id) {
                    guard isKeyFrame else { continue }
                    self.awaitingKeyFrame.remove(id)
                }

                if self.sendingConnectionIds.contains(id) {
                    if isKeyFrame {
                        // A keyframe doesn't depend on anything, so it may replace whatever is queued.
                        if self.pendingFramePackets[id] != nil {
                            self.droppedPendingFrames += 1
                            self.droppedForStats &+= 1
                        }
                        self.pendingFramePackets[id] = packet
                    } else if self.pendingFramePackets[id] == nil {
                        self.pendingFramePackets[id] = packet
                    } else {
                        // Already one frame queued behind the one in flight: dropping this frame
                        // breaks the reference chain, so resynchronise on a fresh keyframe.
                        self.droppedPendingFrames += 1
                        self.droppedForStats &+= 1
                        self.awaitingKeyFrame.insert(id)
                        keyFrameNeeded = true
                    }
                    self.reportDroppedFramesIfNeeded()
                } else {
                    self.sendPacket(packet, to: conn, kind: .frame)
                }
            }

            if keyFrameNeeded {
                self.requestKeyFrame()
            }
        }
    }

    /// Asks the encoder for an immediate keyframe (wired up in Main.swift).
    /// Don't call directly; use requestKeyFrame(), which rate-limits it.
    var onKeyFrameNeeded: (() -> Void)?
    private var awaitingKeyFrame: Set<ObjectIdentifier> = []

    private static let minKeyFrameRequestInterval: TimeInterval = 0.5
    private var lastKeyFrameRequestAt = Date.distantPast
    private var deferredKeyFrameRequestScheduled = false

    /// Rate-limited keyframe request (call on `queue`). Keyframes are large, so on a congested
    /// link asking for one per dropped frame would make the backlog worse; and a client can
    /// send FRAME_REQUEST. At most one request per 0.5 s goes to the encoder. Extra requests
    /// within that window collapse into one deferred request, sent only if a viewer is
    /// still waiting for a keyframe.
    private func requestKeyFrame() {
        let elapsed = Date().timeIntervalSince(lastKeyFrameRequestAt)
        if elapsed >= Self.minKeyFrameRequestInterval {
            lastKeyFrameRequestAt = Date()
            onKeyFrameNeeded?()
            return
        }

        guard !deferredKeyFrameRequestScheduled else { return }
        deferredKeyFrameRequestScheduled = true
        queue.asyncAfter(deadline: .now() + (Self.minKeyFrameRequestInterval - elapsed)) { [weak self] in
            guard let self else { return }
            self.deferredKeyFrameRequestScheduled = false
            guard !self.awaitingKeyFrame.isEmpty else { return }
            self.lastKeyFrameRequestAt = Date()
            self.onKeyFrameNeeded?()
        }
    }

    func sendRealtime(data: Data, packetType: PacketType) {
        var packet = Data(capacity: PacketHeader.size + data.count)
        let header = PacketHeader(type: packetType, payloadLength: UInt32(data.count))
        packet.append(header.encode())
        packet.append(data)

        queue.async { [weak self] in
            guard let self, !self.connections.isEmpty else { return }

            for conn in self.connections {
                let id = ObjectIdentifier(conn)
                if self.sendingConnectionIds.contains(id) {
                    self.pendingRealtimePackets[id] = packet
                } else {
                    self.sendPacket(packet, to: conn, kind: .realtime)
                }
            }
        }
    }

    /// Paired PCs and this Mac's key fingerprint, for the menu bar. Any thread.
    func pairingInfo() -> (names: [String], fingerprint: String) {
        syncOnQueue {
            (pairing.store.trustedNames, pairing.store.identityFingerprint)
        }
    }

    /// Forgets every paired PC and closes any connection from one, including one that is
    /// still pairing (menu bar). The PC has to pair again with the 6-digit code. Any thread.
    func forgetPairedClients() {
        queue.async { [weak self] in
            guard let self else { return }
            self.pairing.store.forgetAll()
            for handshake in Array(self.handshakes.values) {
                handshake.abort("the Mac forgot all paired PCs")
            }
            for conn in self.connections {
                print("\(ServerText.serverTag) closing \(conn.endpoint): paired PCs were forgotten")
                conn.cancel()
                self.removeConnection(conn)
            }
        }
    }

    /// Sends a CLIPBOARD packet to viewers that sent HELLO. Unlike sendRealtime, a stats packet
    /// can't replace it: while a send is in flight it waits in its own slot, and goes first.
    func sendClipboard(_ data: Data) {
        var packet = Data(capacity: PacketHeader.size + data.count)
        packet.append(PacketHeader(type: .clipboard, payloadLength: UInt32(data.count)).encode())
        packet.append(data)

        queue.async { [weak self] in
            guard let self else { return }
            for conn in self.connections {
                let id = ObjectIdentifier(conn)
                guard self.helloConnectionIds.contains(id) else { continue }
                if self.sendingConnectionIds.contains(id) {
                    self.pendingClipboardPackets[id] = packet
                } else {
                    self.sendPacket(packet, to: conn, kind: .realtime)
                }
            }
        }
    }

    /// Sends an INPUT_FEEDBACK packet (MacInputInjector) to viewers that sent HELLO.
    func sendInputFeedback(_ data: Data) {
        var packet = Data(capacity: PacketHeader.size + data.count)
        packet.append(PacketHeader(type: .inputFeedback, payloadLength: UInt32(data.count)).encode())
        packet.append(data)

        queue.async { [weak self] in
            guard let self else { return }
            for conn in self.connections {
                let id = ObjectIdentifier(conn)
                guard self.helloConnectionIds.contains(id) else { continue }
                if self.sendingConnectionIds.contains(id) {
                    var waiting = self.pendingFeedbackPackets[id] ?? []
                    if waiting.count < 16 { waiting.append(packet) }
                    self.pendingFeedbackPackets[id] = waiting
                } else {
                    self.sendPacket(packet, to: conn, kind: .realtime)
                }
            }
        }
    }

    private func sendPacket(_ packet: Data, to conn: NWConnection, kind: PacketKind) {
        let id = ObjectIdentifier(conn)
        // Every packet is encrypted here, at the moment it is handed to the connection, so the
        // record counter always follows the order the bytes go out in.
        guard let cipher = sessions[id], let record = try? cipher.seal(packet) else {
            print("\(ServerText.serverTag) not sending to an unauthenticated connection; closing it")
            conn.cancel()
            removeConnection(conn)
            return
        }
        sendingConnectionIds.insert(id)
        let startedAt = Date()
        sendStartedAt[id] = startedAt
        scheduleSendTimeout(for: conn, id: id, startedAt: startedAt)

        conn.send(content: record, completion: .contentProcessed { [weak self] error in
            guard let self else { return }

            self.queue.async {
                if let error = error {
                    print("\(ServerText.serverTag) send failed: \(error)")
                    self.removeConnection(conn)
                    return
                }

                if let pending = self.nextPendingPacket(after: kind, for: id),
                   self.connections.contains(where: { $0 === conn }) {
                    self.sendPacket(pending.packet, to: conn, kind: pending.kind)
                } else {
                    self.sendingConnectionIds.remove(id)
                    self.sendStartedAt.removeValue(forKey: id)
                }
            }
        })
    }

    private func scheduleSendTimeout(for conn: NWConnection, id: ObjectIdentifier, startedAt: Date) {
        let timeout = sendTimeout()
        queue.asyncAfter(deadline: .now() + timeout) { [weak self, weak conn] in
            guard let self, let conn else { return }
            guard self.connections.contains(where: { $0 === conn }),
                  self.sendingConnectionIds.contains(id),
                  self.sendStartedAt[id] == startedAt else {
                return
            }

            print("\(ServerText.serverTag) send timeout \(String(format: "%.1f", timeout))s, resetting client connection")
            conn.cancel()
            self.removeConnection(conn)
        }
    }

    private func sendTimeout() -> TimeInterval {
        if let value = environmentValue("MACSTREAM_SEND_TIMEOUT_MS"),
           let parsed = Double(value),
           parsed >= 500,
           parsed <= 30_000 {
            return parsed / 1000.0
        }

        return 3.0
    }

    private func nextPendingPacket(after kind: PacketKind, for id: ObjectIdentifier) -> (packet: Data, kind: PacketKind)? {
        // Waiting input feedback goes first (tiny, and the PC's mouse waits for it), then a
        // waiting clipboard packet; otherwise unchanged.
        if var waiting = pendingFeedbackPackets[id], !waiting.isEmpty {
            let packet = waiting.removeFirst()
            pendingFeedbackPackets[id] = waiting.isEmpty ? nil : waiting
            return (packet, .realtime)
        }
        if let packet = pendingClipboardPackets.removeValue(forKey: id) {
            return (packet, .realtime)
        }
        switch kind {
        case .frame:
            if let packet = pendingRealtimePackets.removeValue(forKey: id) {
                return (packet, .realtime)
            }
            if let packet = pendingFramePackets.removeValue(forKey: id) {
                return (packet, .frame)
            }
        case .realtime:
            if let packet = pendingFramePackets.removeValue(forKey: id) {
                return (packet, .frame)
            }
            if let packet = pendingRealtimePackets.removeValue(forKey: id) {
                return (packet, .realtime)
            }
        }

        return nil
    }

    private func removeConnection(_ conn: NWConnection) {
        let hadConnections = !connections.isEmpty
        let id = ObjectIdentifier(conn)
        connections.removeAll { $0 === conn }
        sendingConnectionIds.remove(id)
        sendStartedAt.removeValue(forKey: id)
        pendingFramePackets.removeValue(forKey: id)
        pendingRealtimePackets.removeValue(forKey: id)
        pendingClipboardPackets.removeValue(forKey: id)
        pendingFeedbackPackets.removeValue(forKey: id)
        receiveBuffers.removeValue(forKey: id)
        awaitingKeyFrame.remove(id)
        helloConnectionIds.remove(id)
        sessions.removeValue(forKey: id)
        handshakes.removeValue(forKey: id)?.cancel()

        if hadConnections && connections.isEmpty {
            firstClientNotificationSent = false
            lastClientDisplayInfo = nil
            onLastClientDisconnected?()
        }
    }

    private func reportDroppedFramesIfNeeded() {
        let now = Date()
        guard droppedPendingFrames > 0, now.timeIntervalSince(lastDropReportTime) >= 1.0 else { return }

        print("\(ServerText.serverTag) send queue dropped stale frames: \(droppedPendingFrames)")
        droppedPendingFrames = 0
        lastDropReportTime = now
    }

    private func receiveControlPackets(from conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 65_536) { [weak self, weak conn] data, _, isComplete, error in
            guard let self, let conn else { return }

            self.queue.async {
                let id = ObjectIdentifier(conn)

                if let data, !data.isEmpty {
                    var encrypted = data
                    if let handshake = self.handshakes[id] {
                        handshake.receive(data)   // may finish and promote() the connection
                        encrypted = handshake.leftover
                    }
                    if let cipher = self.sessions[id], !encrypted.isEmpty {
                        do {
                            var buffer = self.receiveBuffers[id] ?? Data()
                            for packet in try cipher.open(encrypted) {
                                buffer.append(packet)
                            }
                            self.receiveBuffers[id] = buffer
                            self.processControlBuffer(for: conn)
                        } catch {
                            print("\(ServerText.serverTag) decryption failed (\(error)); closing the connection")
                            conn.cancel()
                            self.removeConnection(conn)
                            return
                        }
                    }
                }

                if isComplete || error != nil {
                    if let error {
                        print("\(ServerText.serverTag) control receive failed: \(error)")
                    }
                    self.removeConnection(conn)
                    return
                }

                if self.connections.contains(where: { $0 === conn }) || self.handshakes[id] != nil {
                    self.receiveControlPackets(from: conn)
                }
            }
        }
    }

    private func processControlBuffer(for conn: NWConnection) {
        let id = ObjectIdentifier(conn)
        var buffer = receiveBuffers[id] ?? Data()

        while buffer.count >= PacketHeader.size {
            let headerData = buffer.prefix(PacketHeader.size)
            guard let header = PacketHeader.decode(Data(headerData)) else {
                print("\(ServerText.serverTag) received invalid control packet header, dropping \(buffer.count) bytes")
                buffer.removeAll()
                break
            }

            if header.payloadLength > UInt32(FrameServer.maxControlPayloadLength) {
                print("\(ServerText.serverTag) control packet too large: \(header.payloadLength)")
                buffer.removeAll()
                break
            }

            let packetSize = PacketHeader.size + Int(header.payloadLength)
            guard buffer.count >= packetSize else {
                break
            }

            let payload = buffer.subdata(in: PacketHeader.size..<packetSize)
            buffer.removeSubrange(0..<packetSize)
            handleControlPacket(header: header, payload: payload, from: conn)
        }

        receiveBuffers[id] = buffer
    }

    private func handleControlPacket(header: PacketHeader, payload: Data, from conn: NWConnection) {
        switch header.type {
        case .hello:
            guard payload.count >= 12 else {
                print("\(ServerText.serverTag) HELLO payload too short: \(payload.count)")
                return
            }

            var info = ClientDisplayInfo(
                width: Int(readUInt32(payload, offset: 0)),
                height: Int(readUInt32(payload, offset: 4)),
                refreshRate: Int(readUInt32(payload, offset: 8)),
                dpi: payload.count >= 16 ? Int(readUInt32(payload, offset: 12)) : 0)
            if payload.count >= 32 {
                info.screen = ScreenRequest(
                    extraDisplay: readUInt32(payload, offset: 16) == 1,
                    textScale: Int(min(readUInt32(payload, offset: 20), 2)),
                    width: Int(min(readUInt32(payload, offset: 24), 16_384)),
                    height: Int(min(readUInt32(payload, offset: 28), 16_384)))
            }
            lastClientDisplayInfo = info
            helloConnectionIds.insert(ObjectIdentifier(conn))
            let screenText: String
            if let screen = info.screen, screen.extraDisplay {
                let size = screen.width > 0 ? "\(screen.width)x\(screen.height)" : "PC monitor size"
                screenText = "extra display (\(size), text scale \(screen.textScale == 0 ? "auto" : "\(screen.textScale)x"))"
            } else {
                screenText = "mirror this Mac's screen"
            }
            print("\(ServerText.serverTag) received client display info: \(info.width)x\(info.height) @ \(info.refreshRate)Hz, dpi=\(info.dpi), wants: \(screenText)")
            notifyFirstClientIfNeeded(info: info)

        case .frameRequest:
            // The viewer lost its decoding reference (e.g. a decode error) and wants a keyframe.
            // It drops predicted frames until then anyway, so stop sending them (saves bandwidth),
            // and go through the rate limiter.
            awaitingKeyFrame.insert(ObjectIdentifier(conn))
            requestKeyFrame()

        case .stats:
            // Ping (kind 2, u64 client token): echo it straight back as kind 3 so the client can
            // measure round-trip time over the link. Anything else from the client is ignored.
            guard payload.count == 9, payload.first == 2 else { break }
            var pong = payload
            pong[pong.startIndex] = 3
            sendRealtime(data: pong, packetType: .stats)

        case .inputEvent:
            // Only from a paired viewer (this point is only reached for authenticated,
            // encrypted connections) that has also sent HELLO.
            guard sessions[ObjectIdentifier(conn)] != nil,
                  helloConnectionIds.contains(ObjectIdentifier(conn)) else { break }
            onInputEvent?(payload)

        case .clipboard:
            // Same rule as input: paired, encrypted, and HELLO sent.
            guard sessions[ObjectIdentifier(conn)] != nil,
                  helloConnectionIds.contains(ObjectIdentifier(conn)) else { break }
            onClipboard?(payload)

        default:
            break
        }
    }

    private func notifyFirstClientAfterHelloTimeout() {
        queue.asyncAfter(deadline: .now() + .milliseconds(300)) { [weak self] in
            guard let self, !self.connections.isEmpty else { return }
            self.notifyFirstClientIfNeeded(info: self.lastClientDisplayInfo)
        }
    }

    private func notifyFirstClientIfNeeded(info: ClientDisplayInfo?) {
        guard !firstClientNotificationSent else { return }
        firstClientNotificationSent = true
        onFirstClientConnected?(info)
    }

    private func syncOnQueue<T>(_ work: () -> T) -> T {
        if DispatchQueue.getSpecific(key: Self.queueKey) != nil {
            return work()
        }

        return queue.sync(execute: work)
    }
}
