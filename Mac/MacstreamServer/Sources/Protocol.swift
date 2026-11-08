import Foundation
import CoreGraphics

// MARK: - Protocol Constants

enum PacketType: UInt16 {
    case hello = 0x0001
    case helloAck = 0x0002
    case displayInfo = 0x0010
    case frameRequest = 0x0020
    case frameH264 = 0x0030
    case frameRaw = 0x0031
    case frameHevc = 0x0032
    case inputEvent = 0x0040
    case inputFeedback = 0x0041   // Mac -> PC: where the PC's mouse is (own screen or not)
    case stats = 0x0050
    case clipboard = 0x0080   // clipboard sync, both directions (see ClipboardSync.swift)
    // 0x0060 (separate cursor channel) and 0x0070/0x0071 (Mac -> Windows control) were
    // removed from the original fork; the values stay reserved.
    // Security handshake (plaintext, before anything else; see PROTOCOL.md)
    case authClientHello = 0x0090
    case authServerHello = 0x0091
    case pairNonce = 0x0092
    case pairReveal = 0x0093
    case pairResult = 0x0094
}

let ProtocolMagic: UInt16 = 0x574D  // "WM"
let ProtocolVersion: UInt16 = 0x0001
let ControlPort: UInt16 = 9801
let VideoPort: UInt16 = 9802

func videoDimensionAlignment() -> Int {
    if let value = environmentValue("MACSTREAM_ALIGN"),
       let parsed = Int(value),
       parsed >= 2,
       parsed <= 128 {
        return parsed
    }

    return 2
}

func alignVideoDimension(_ value: Int) -> Int {
    let alignment = videoDimensionAlignment()
    return max(alignment, (value / alignment) * alignment)
}

// MARK: - Packet Header (10 bytes)

struct PacketHeader {
    let type: PacketType
    let payloadLength: UInt32

    static let size = 10

    func encode() -> Data {
        var data = Data(capacity: PacketHeader.size)
        var m = ProtocolMagic.littleEndian
        var v = ProtocolVersion.littleEndian
        var t = type.rawValue.littleEndian
        var p = payloadLength.littleEndian

        data.append(Data(bytes: &m, count: 2))
        data.append(Data(bytes: &v, count: 2))
        data.append(Data(bytes: &t, count: 2))
        data.append(Data(bytes: &p, count: 4))
        return data
    }

    static func decode(_ data: Data) -> PacketHeader? {
        guard data.count >= PacketHeader.size else { return nil }
        guard readUInt16(data, offset: 0) == ProtocolMagic else { return nil }
        guard readUInt16(data, offset: 2) == ProtocolVersion else { return nil }
        guard let packetType = PacketType(rawValue: readUInt16(data, offset: 4)) else { return nil }
        let payloadLength = readUInt32(data, offset: 6)
        return PacketHeader(type: packetType, payloadLength: payloadLength)
    }
}

func readUInt32(_ data: Data, offset: Int) -> UInt32 {
    UInt32(data[offset]) |
        (UInt32(data[offset + 1]) << 8) |
        (UInt32(data[offset + 2]) << 16) |
        (UInt32(data[offset + 3]) << 24)
}

private func readUInt16(_ data: Data, offset: Int) -> UInt16 {
    UInt16(data[offset]) |
        (UInt16(data[offset + 1]) << 8)
}
