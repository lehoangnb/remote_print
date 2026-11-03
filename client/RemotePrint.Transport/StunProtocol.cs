using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RemotePrint.Service;

// RFC 8489 IPv4 Binding. Responses are additionally pinned to the requested
// server endpoint and random transaction by MeshTransport.
public static class StunProtocol
{
    // Remote Print's STUN listener is private to authorized mesh sessions.
    // The RFC 8489 USERNAME attribute carries the existing, random 64-byte
    // session cookie. The 88-byte request is larger than its 32-byte reply,
    // avoiding unauthenticated reflection/amplification.
    public static byte[] AuthenticatedRequest(string cookie)
    {
        if (cookie.Length != 64 || !cookie.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid mesh STUN cookie.", nameof(cookie));
        var packet = new byte[88];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 68);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x2112A442);
        RandomNumberGenerator.Fill(packet.AsSpan(8, 12));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 0x0006); // USERNAME
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 64);
        Encoding.ASCII.GetBytes(cookie).CopyTo(packet, 24);
        return packet;
    }

    public static IPEndPoint? Response(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> request)
    {
        if (packet.Length < 20 || request.Length != 88 || BinaryPrimitives.ReadUInt16BigEndian(packet) != 0x101 ||
            BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) != 0x2112A442 ||
            !packet.Slice(8, 12).SequenceEqual(request.Slice(8, 12))) return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (length % 4 != 0 || length + 20 != packet.Length) return null;
        for (var offset = 20; offset + 4 <= packet.Length;)
        {
            var kind = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            if (offset + 4 + size > packet.Length) return null;
            var value = packet.Slice(offset + 4, size);
            if (kind == 0x20 && size == 8 && value[1] == 1)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ 0x2112;
                var address = new byte[4];
                for (var i = 0; i < 4; i++) address[i] = (byte)(value[i + 4] ^ request[i + 4]);
                if (port == 0 || address[0] is 0 or 127 or >= 224) return null;
                return new IPEndPoint(new IPAddress(address), port);
            }
            offset += 4 + (size + 3) / 4 * 4;
        }
        return null;
    }
}
