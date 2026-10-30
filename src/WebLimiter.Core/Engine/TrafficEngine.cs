using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WebLimiter.Core.Model;
using WebLimiter.Core.Native;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Engine;

public sealed class WinDivertException : Exception
{
    public WinDivertException(int code) : base(Describe(code)) => Code = code;

    public int Code { get; }

    private static string Describe(int code) => code switch
    {
        2 => "WinDivert64.sys est introuvable à côté de WinDivert.dll.",
        5 => "Accès refusé : WebLimiter doit être lancé en tant qu'administrateur.",
        87 => "Filtre WinDivert invalide.",
        126 => "WinDivert.dll est introuvable.",
        577 => "La signature du pilote WinDivert n'est pas reconnue par Windows.",
        654 => "Une autre version du pilote WinDivert est déjà chargée (redémarrez Windows).",
        1060 or 1275 => "Le chargement du pilote WinDivert a été bloqué (antivirus ou sécurité Windows).",
        1753 => "Le service « Moteur de filtrage de base » (BFE) est arrêté.",
        _ => $"Impossible d'ouvrir WinDivert (erreur Windows {code}).",
    };
}

/// <summary>
/// Diverts every non-loopback IP packet through user mode, attributes it to a process,
/// accounts it, and then drops it (block), delays it (rate limit) or reinjects it.
/// </summary>
public sealed unsafe class TrafficEngine : EngineBase
{
    private const int PacketBufferSize = 4 * 1024 * 1024;
    private const int AddrSize = 80;

    private readonly PortOwnerTable _ports = new();

    private IntPtr _net = WinDivertNative.InvalidHandle;
    private IntPtr _sock = WinDivertNative.InvalidHandle;
    private Thread? _netThread, _sockThread, _schedThread;

    public TrafficEngine(RuleStore rules, StatsStore stats) : base(rules, stats) { }

    public override void Start()
    {
        if (Running) return;
        _ports.Refresh();

        _net = Open("!loopback", WinDivertLayer.Network, 0, WinDivertFlags.None);
        WinDivertNative.WinDivertSetParam(_net, WinDivertParam.QueueLength, 16384);
        WinDivertNative.WinDivertSetParam(_net, WinDivertParam.QueueTime, 2000);
        WinDivertNative.WinDivertSetParam(_net, WinDivertParam.QueueSize, 32 * 1024 * 1024);

        try
        {
            _sock = Open("!loopback and (tcp or udp)", WinDivertLayer.Socket, 1,
                WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        }
        catch (WinDivertException ex)
        {
            // Not fatal: attribution falls back to the IP Helper tables.
            DataPaths.LogError("Couche SOCKET indisponible", ex);
        }

        Running = true;
        Win32.timeBeginPeriod(1);

        _netThread = StartThread(NetworkLoop, "WebLimiter.Network", ThreadPriority.AboveNormal);
        _schedThread = StartThread(SchedulerLoop, "WebLimiter.Scheduler", ThreadPriority.AboveNormal);
        if (_sock != WinDivertNative.InvalidHandle)
            _sockThread = StartThread(SocketLoop, "WebLimiter.Socket", ThreadPriority.Normal);

        BeginTicking();
    }

    public override void Stop()
    {
        if (!Running) return;
        Running = false;
        EndTicking();

        foreach (var h in new[] { _net, _sock })
            if (h != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertShutdown(h, 3);
        _netThread?.Join(2000);
        _sockThread?.Join(2000);
        _schedThread?.Join(2000);
        foreach (var h in new[] { _net, _sock })
            if (h != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(h);
        _net = _sock = WinDivertNative.InvalidHandle;

        foreach (var app in AppList)
        {
            app.DownShaper.Clear();
            app.UpShaper.Clear();
        }
        GlobalDown.Clear();
        GlobalUp.Clear();
        Win32.timeEndPeriod(1);
        ResetSnapshot();
    }

    private static IntPtr Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags)
    {
        IntPtr h;
        try { h = WinDivertNative.WinDivertOpen(filter, layer, priority, flags); }
        catch (DllNotFoundException) { throw new WinDivertException(126); }
        if (h == WinDivertNative.InvalidHandle) throw new WinDivertException(Marshal.GetLastWin32Error());
        return h;
    }

    private static Thread StartThread(ThreadStart body, string name, ThreadPriority priority)
    {
        var t = new Thread(body) { IsBackground = true, Name = name, Priority = priority };
        t.Start();
        return t;
    }

    // ---- packet path ------------------------------------------------------------------

    private void NetworkLoop()
    {
        byte* buffer = (byte*)NativeMemory.Alloc(PacketBufferSize);
        var addrs = (WinDivertAddress*)NativeMemory.Alloc(WinDivertNative.BatchMax * AddrSize);
        try
        {
            while (Running)
            {
                uint addrLen = WinDivertNative.BatchMax * AddrSize;
                if (!WinDivertNative.WinDivertRecvEx(_net, buffer, PacketBufferSize, out uint recvLen, 0,
                        addrs, ref addrLen, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (!Running || err is WinDivertNative.ErrorNoData or WinDivertNative.ErrorInvalidHandle
                            or WinDivertNative.ErrorOperationAborted) break;
                    continue;
                }

                int count = (int)(addrLen / AddrSize);
                byte* p = buffer;
                uint remaining = recvLen;
                for (int i = 0; i < count && remaining > 0; i++)
                {
                    try { remaining = HandlePacket(ref p, remaining, ref addrs[i]); }
                    catch (Exception ex)
                    {
                        DataPaths.LogError("Erreur de traitement de paquet", ex);
                        break;
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
            NativeMemory.Free(addrs);
        }
    }

    /// <summary>Handles the packet at <paramref name="p"/>, advances p and returns the bytes left in the batch.</summary>
    private uint HandlePacket(ref byte* p, uint remaining, ref WinDivertAddress addr)
    {
        byte* ip4 = null, ip6 = null, tcp = null, udp = null, next = null;
        byte proto = 0;
        uint nextLen = 0;
        WinDivertNative.WinDivertHelperParsePacket(p, remaining, &ip4, &ip6, &proto, null, null,
            &tcp, &udp, null, null, &next, &nextLen);

        int length = next != null && next > p ? (int)(next - p) : IpLength(p, remaining);
        byte* packet = p;
        p += length;
        uint left = remaining - (uint)length;

        bool outbound = addr.Outbound;
        UInt128 remote = 0;
        bool v6 = false;
        if (ip4 != null)
        {
            remote = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(outbound ? ip4 + 16 : ip4 + 12, 4));
        }
        else if (ip6 != null)
        {
            v6 = true;
            var span = new ReadOnlySpan<byte>(outbound ? ip6 + 24 : ip6 + 8, 16);
            remote = BinaryPrimitives.ReadUInt128BigEndian(span);
        }

        byte* l4 = tcp != null ? tcp : udp;
        ushort localPort = 0, remotePort = 0;
        AppTraffic app;
        if (l4 != null)
        {
            ushort src = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(l4, 2));
            ushort dst = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(l4 + 2, 2));
            localPort = outbound ? src : dst;
            remotePort = outbound ? dst : src;
            proto = tcp != null ? (byte)6 : (byte)17;
            app = AppFor(_ports.Lookup(proto, localPort));
        }
        else
        {
            app = AppFor(4); // ICMP and other protocols belong to the system
        }

        var connection = app.Connections.GetOrAdd(new ConnKey(proto, localPort, remote, remotePort, v6),
            static _ => new Counter());
        connection.LastSeen = Environment.TickCount64;

        if (app.Blocked) return left; // drop

        var shaper = outbound ? app.UpShaper : app.DownShaper;
        var verdict = shaper.Admit(packet, length, addr, app.Counter, connection);
        if (verdict == Verdict.Pass)
            verdict = (outbound ? GlobalUp : GlobalDown).Admit(packet, length, addr, app.Counter, connection);
        if (verdict == Verdict.Pass)
        {
            fixed (WinDivertAddress* a = &addr)
                WinDivertNative.WinDivertSend(_net, packet, (uint)length, out _, a);
            app.Counter.Add(outbound, length);
            connection.Add(outbound, length);
        }
        return left;
    }

    private static int IpLength(byte* p, uint remaining)
    {
        int len = 0;
        if (remaining >= 20 && p[0] >> 4 == 4) len = (p[2] << 8) | p[3];
        else if (remaining >= 40 && p[0] >> 4 == 6) len = 40 + ((p[4] << 8) | p[5]);
        return len <= 0 || len > remaining ? (int)remaining : len;
    }

    private void SchedulerLoop()
    {
        var released = new List<HeldPacket>(512);
        var ready = new List<HeldPacket>(512);
        while (Running)
        {
            foreach (var app in AppList)
            {
                if (app.DownShaper.HasQueue) app.DownShaper.Release(released, app.Blocked);
                if (app.UpShaper.HasQueue) app.UpShaper.Release(released, app.Blocked);
            }
            // Packets let through by their app's shaper still have to cross the global one.
            foreach (var held in released)
            {
                var global = held.Address.Outbound ? GlobalUp : GlobalDown;
                Verdict verdict;
                fixed (byte* b = held.Buffer)
                    verdict = global.Admit(b, held.Length, held.Address, held.App, held.Connection);
                if (verdict == Verdict.Pass) ready.Add(held);
                else ArrayPool<byte>.Shared.Return(held.Buffer); // held: the global shaper keeps its own copy
            }
            released.Clear();
            if (GlobalDown.HasQueue) GlobalDown.Release(ready, false);
            if (GlobalUp.HasQueue) GlobalUp.Release(ready, false);

            foreach (var held in ready)
            {
                var addr = held.Address;
                fixed (byte* b = held.Buffer)
                    WinDivertNative.WinDivertSend(_net, b, (uint)held.Length, out _, &addr);
                held.App.Add(addr.Outbound, held.Length);
                held.Connection.Add(addr.Outbound, held.Length);
                ArrayPool<byte>.Shared.Return(held.Buffer);
            }
            ready.Clear();
            Thread.Sleep(1);
        }
    }

    // ---- socket events (process attribution) ------------------------------------------

    private void SocketLoop()
    {
        var addrs = (WinDivertAddress*)NativeMemory.Alloc(WinDivertNative.BatchMax * AddrSize);
        try
        {
            while (Running)
            {
                uint addrLen = WinDivertNative.BatchMax * AddrSize;
                if (!WinDivertNative.WinDivertRecvEx(_sock, null, 0, out _, 0, addrs, ref addrLen, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (!Running || err is WinDivertNative.ErrorNoData or WinDivertNative.ErrorInvalidHandle
                            or WinDivertNative.ErrorOperationAborted) break;
                    continue;
                }
                int count = (int)(addrLen / AddrSize);
                for (int i = 0; i < count; i++)
                {
                    ref var a = ref addrs[i];
                    Trace?.Invoke($"sock {a.Event} proto={a.Protocol} lport={a.LocalPort} rport={a.RemotePort} pid={a.ProcessId}");
                    if (a.Event is WinDivertEvent.SocketBind or WinDivertEvent.SocketConnect
                            or WinDivertEvent.SocketListen or WinDivertEvent.SocketAccept
                        && a.LocalPort != 0 && a.Protocol is 6 or 17)
                    {
                        _ports.Set(a.Protocol, a.LocalPort, (int)a.ProcessId);
                    }
                }
            }
        }
        finally { NativeMemory.Free(addrs); }
    }
}
