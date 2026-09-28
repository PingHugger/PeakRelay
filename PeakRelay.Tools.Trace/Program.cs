using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PeakRelay.Protocol;

namespace PeakRelay.Tools.Trace;

/// <summary>
/// Summarizes JSONL capture files produced by the client shim. One JSON object per line:
///   {"t": "12:34:56.789", "dir": "send"|"recv", "bytes": N, "cmds": [...]}
/// Each cmds entry: {"ty": int, "ch": int, "f": int, "sq": int, "pl": int}.
/// Malformed or undecodable lines are reported, never silently skipped.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: dotnet run --project PeakRelay.Tools.Trace -- <capture.jsonl>");
            return 2;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"file not found: {path}");
            return 2;
        }

        var stats = new Dictionary<string, int>();
        int lineNo = 0, datagrams = 0, commands = 0, malformed = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                Console.WriteLine($"{lineNo}: MALFORMED JSON");
                malformed++;
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var dir = root.TryGetProperty("dir", out var d) ? d.GetString() : "?";
                var bytes = root.TryGetProperty("bytes", out var b) ? b.GetInt32() : 0;
                Console.Write($"{lineNo,6}  {dir,-4}  {bytes,6}B");

                if (root.TryGetProperty("cmds", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
                {
                    datagrams++;
                    foreach (var cmd in cmds.EnumerateArray())
                    {
                        var ty = cmd.TryGetProperty("ty", out var t) ? t.GetInt32() : -1;
                        var ch = cmd.TryGetProperty("ch", out var c) ? c.GetInt32() : -1;
                        var flags = cmd.TryGetProperty("f", out var f) ? f.GetInt32() : -1;
                        var payload = cmd.TryGetProperty("pl", out var pl) ? pl.GetInt32() : 0;
                        var name = PhotonDatagram.CommandTypeName((byte)Math.Max(ty, 0));
                        var rel = (flags & 1) != 0 ? "[R]" : "[U]";
                        Console.Write($"  {name}{rel}(ch{ch} pl{payload})");
                        stats[name] = stats.GetValueOrDefault(name) + 1;
                        commands++;
                    }
                }
                else
                {
                    Console.Write("  (no cmds field)");
                    malformed++;
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"lines={lineNo} datagrams={datagrams} commands={commands} malformed={malformed}");
        if (stats.Count > 0)
        {
            Console.WriteLine("command histogram:");
            foreach (var (name, count) in stats.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {name,-24} {count,6}");
        }

        return malformed == 0 ? 0 : 1;
    }
}
