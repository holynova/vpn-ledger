using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace VpnLedger;

// Windows IP Helper owner tables. Read once per snapshot, never one shell per connection.
internal sealed class Owners
{
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    private readonly Dictionary<(bool Tcp, string IP, int Port), List<(string Peer, int Port, int Pid)>> rows = new();
    private readonly Dictionary<int, (string App, string? Path)> names = new();
    public Owners()
    {
        foreach (int family in new[] { 2, 23 })
        {
            Read(family, true);
            Read(family, false);
        }
    }
    internal static string Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString().Split('%')[0];
    private void Read(int family, bool tcp)
    {
        uint Fetch(IntPtr ptr, ref int n) => tcp ? GetExtendedTcpTable(ptr, ref n, false, family, 5, 0)
                                                : GetExtendedUdpTable(ptr, ref n, false, family, 1, 0);
        int size = 0;
        Fetch(IntPtr.Zero, ref size);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = Fetch(buffer, ref size);
                if (result == 122) continue;
                if (result != 0) return;
                bool v6 = family == 23;
                int stride = tcp ? (v6 ? 56 : 24) : (v6 ? 28 : 12);
                var row = new byte[stride];
                int count = Math.Min(Marshal.ReadInt32(buffer), (size - 4) / stride);
                for (int i = 0; i < count; i++)
                {
                    Marshal.Copy(buffer + 4 + i * stride, row, 0, stride);
                    int offset = tcp && !v6 ? 4 : 0;
                    string Address(int at) => Normalize(new IPAddress(row.AsSpan(at, v6 ? 16 : 4)));
                    int Port(int at) => BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(at, 2));
                    int pid = BitConverter.ToInt32(row, stride - 4);
                    if (pid <= 0) continue;
                    var key = (tcp, Address(offset), Port(offset + (v6 ? 20 : 4)));
                    if (!rows.TryGetValue(key, out var list)) rows[key] = list = new();
                    list.Add((tcp ? Address(v6 ? 24 : 12) : "", tcp ? Port(v6 ? 44 : 16) : 0, pid));
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    public (string App, string? Path) Find(bool tcp, string ip, int port, string peer, int peerPort, bool omittedFakeIp = false)
    {
        var fallback = (Ledger.UnknownApp, (string?)null);
        rows.TryGetValue((tcp, ip, port), out var exact);
        List<(string Peer, int Port, int Pid)>? wildcard = null;
        if (!tcp) rows.TryGetValue((false, ip.Contains(':') ? "::" : "0.0.0.0", port), out wildcard);
        int pid = 0;
        foreach (var list in new[] { exact, wildcard })
        {
            if (list == null) continue;
            foreach (var row in list)
            {
                if (tcp && (row.Port != peerPort || (row.Peer != peer && !omittedFakeIp))) continue;
                if (pid != 0 && pid != row.Pid) return fallback;
                pid = row.Pid;
            }
        }
        if (pid == 0) return fallback;
        if (names.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var p = Process.GetProcessById(pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { }
            var name = path == null ? p.ProcessName + ".exe" : Path.GetFileName(path);
            return names[pid] = (name.ToLowerInvariant(), path);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return fallback; }
    }
}
