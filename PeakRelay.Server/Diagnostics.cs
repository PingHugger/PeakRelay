using System;
using System.Text;

namespace PeakRelay.Server;

/// <summary>Console hex-dump tracing (PEAKRELAY_TRACE=1) for debugging the relay handshake.</summary>
public static class Diagnostics
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("PEAKRELAY_TRACE") == "1";

    public static void Dump(string direction, byte[] data, int length)
    {
        if (!Enabled)
            return;
        var sb = new StringBuilder(length * 3 + 32);
        sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ").Append(direction)
          .Append(' ').Append(length).Append("B: ");
        for (int i = 0; i < Math.Min(length, 96); i++)
            sb.Append(data[i].ToString("x2")).Append(' ');
        Console.WriteLine(sb.ToString());
    }
}
