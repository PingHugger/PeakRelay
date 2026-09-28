using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PeakRelay.Protocol;

namespace PeakRelay.Client;

/// <summary>
/// Best-effort JSONL trace writer. The network/Photon threads only enqueue pre-formatted
/// lines (never block); a background thread owns the collector TCP connection. If no
/// collector is running, records are dropped silently and the game behaves normally.
/// </summary>
public sealed class TraceRecorder : IDisposable
{
    public static TraceRecorder Instance { get; } = new();

    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _writer;
    private TcpClient? _client;
    private StreamWriter? _stream;
    private volatile bool _enabled;
    private int _port = 5060;
    private int _dropped;
    private int _disposed;

    private TraceRecorder()
    {
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "peakrelay-trace" };
        _writer.Start();
    }

    public void Configure(bool enabled, int port)
    {
        _enabled = enabled;
        _port = port;
    }

    public bool Enabled => _enabled;

    /// <summary>Copy-and-log one datagram. Safe to call from Photon's send/receive threads.</summary>
    public void LogDatagram(string direction, byte[] buffer, int length)
    {
        if (!_enabled || length <= 0)
            return;
        string line;
        try
        {
            line = FormatLine(direction, buffer, length);
        }
        catch
        {
            return; // decoding must never affect gameplay
        }
        if (!_queue.TryAdd(line))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Logs a lifecycle event (socket created, connected, ...). Never blocks.</summary>
    public void LogLifecycle(string message)
    {
        if (!_enabled)
            return;
        var line = "{\"t\":\"" + DateTime.Now.ToString("HH:mm:ss.fff") +
                  "\",\"dir\":\"life\",\"bytes\":0,\"msg\":\"" + message.Replace("\"", "'") + "\"}";
        if (!_queue.TryAdd(line))
            Interlocked.Increment(ref _dropped);
    }

    private static string FormatLine(string direction, byte[] buffer, int length)
    {
        var sb = new StringBuilder(128);
        sb.Append("{\"t\":\"").Append(DateTime.Now.ToString("HH:mm:ss.fff"));
        sb.Append("\",\"dir\":\"").Append(direction);
        sb.Append("\",\"bytes\":").Append(length);
        sb.Append(",\"cmds\":[");
        var commands = PhotonDatagram.ReadCommands(buffer.AsSpan(0, length));
        bool first = true;
        foreach (var cmd in commands)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append("{\"ty\":").Append(cmd.CommandType);
            sb.Append(",\"ch\":").Append(cmd.ChannelId);
            sb.Append(",\"f\":").Append(cmd.Flags);
            sb.Append(",\"sq\":").Append(cmd.ReliableSeq);
            sb.Append(",\"pl\":").Append(cmd.PayloadLength);
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private void WriterLoop()
    {
        var reconnectIn = TimeSpan.FromSeconds(2);
        while (!_queue.IsCompleted)
        {
            try
            {
                if (_stream == null)
                {
                    if (!_enabled)
                    {
                        Thread.Sleep(500);
                        continue;
                    }
                    if (!TryConnect())
                    {
                        // No collector running; drain and drop so the queue never backs up.
                        while (_queue.TryTake(out _))
                            Interlocked.Increment(ref _dropped);
                        Thread.Sleep(reconnectIn);
                        continue;
                    }
                }

                foreach (var line in _queue.GetConsumingEnumerable())
                {
                    _stream!.WriteLine(line);
                    // Flush in small batches; the collector tolerates partial lines.
                    if (_queue.Count == 0)
                        _stream.Flush();
                }
            }
            catch
            {
                TearDownConnection();
                Thread.Sleep(reconnectIn);
            }
        }
    }

    private bool TryConnect()
    {
        try
        {
            _client = new TcpClient();
            var task = _client.ConnectAsync("127.0.0.1", _port);
            if (!task.Wait(500) || !_client.Connected)
            {
                _client.Close();
                _client = null;
                return false;
            }
            _stream = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { AutoFlush = false };
            return true;
        }
        catch
        {
            TearDownConnection();
            return false;
        }
    }

    private void TearDownConnection()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _queue.CompleteAdding();
        _writer.Join(1000);
        TearDownConnection();
        _queue.Dispose();
        if (_dropped > 0)
            Trace.WriteLine($"PeakRelay trace: {_dropped} records dropped (queue full / no collector)");
    }
}
