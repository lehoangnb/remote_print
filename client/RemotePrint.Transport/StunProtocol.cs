using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace RemotePrint.Service;

// RFC 8489 IPv4 Binding. Responses are additionally pinned to the requested
// server endpoint and random transaction by MeshTransport.
public static class StunProtocol
{
    public static byte[] Request()
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x2112A442);
        RandomNumberGenerator.Fill(packet.AsSpan(8));
        return packet;
    }

    public static IPEndPoint? Response(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> request)
    {
        if (packet.Length < 20 || request.Length != 20 || BinaryPrimitives.ReadUInt16BigEndian(packet) != 0x101 ||
            BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) != 0x2112A442 || !packet.Slice(8, 12).SequenceEqual(request[8..])) return null;
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
