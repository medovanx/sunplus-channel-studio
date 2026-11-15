using System.Buffers.Binary;
using SunplusChannelStudio.Core;

namespace SunplusChannelStudio.Tests;

public static class Samples
{
    /// <summary>Builds a small database by hand, laid out exactly as the receiver writes it.</summary>
    public static byte[] Database(int channelSize = 92)
    {
        var sat = new byte[36];
        sat[0] = 0x1e; // as the receiver stores Nilesat: Ku band, west
        "Nilesat"u8.CopyTo(sat.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(sat.AsSpan(24), 70);
        BinaryPrimitives.WriteUInt16LittleEndian(sat.AsSpan(28), 9750);
        BinaryPrimitives.WriteUInt16LittleEndian(sat.AsSpan(30), 10600);

        var tp = new byte[36];
        "TP 1"u8.CopyTo(tp);
        BinaryPrimitives.WriteUInt16LittleEndian(tp.AsSpan(16), 11766);
        BinaryPrimitives.WriteUInt16LittleEndian(tp.AsSpan(18), 27500);
        tp[24] = 1;

        byte[] Channel(byte kind, string name, ushort sid)
        {
            var c = new byte[channelSize];
            c[0] = 1;
            c[1] = kind;
            System.Text.Encoding.Latin1.GetBytes(name).CopyTo(c, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(46), sid);
            BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(56), 0x4000 | 1302); // flagged video PID
            return c;
        }

        var header = new byte[24];
        "db15"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x153);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);  // transponders
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 2); // TV
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 1); // radio
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1); // satellites
        return [.. header, .. sat, .. tp, .. Channel(1, "One", 10), .. Channel(1, "Two", 20), .. Channel(2, "Radio", 30)];
    }
}
