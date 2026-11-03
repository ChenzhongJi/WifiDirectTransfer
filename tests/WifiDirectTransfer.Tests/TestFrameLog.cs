using System.Buffers.Binary;
using System.IO;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.Tests;

internal static class TestFrameLog
{
    public static List<(MessageType Type, byte[] Payload)> Parse(
        MemoryStream log)
    {
        var bytes = log.ToArray();
        var frames = new List<(MessageType Type, byte[] Payload)>();
        var offset = 0;

        while (offset < bytes.Length)
        {
            if (offset + MessageProtocol.FrameHeaderLength > bytes.Length)
            {
                throw new InvalidDataException("帧日志尾部不完整。");
            }

            var type = (MessageType)bytes[offset];
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(offset + 1, 4)));
            offset += MessageProtocol.FrameHeaderLength;

            if (length < 0 || offset + length > bytes.Length)
            {
                throw new InvalidDataException("帧 payload 长度超出日志范围。");
            }

            frames.Add((type, bytes.AsSpan(offset, length).ToArray()));
            offset += length;
        }

        return frames;
    }
}
