using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace VpnLedger;

public record AdapterReading(string Name, Bytes Counter);
public record AppReading(string Id, string Name, string Identity, string Adapter, Bytes Counter);
public record GlobalSnapshot(Dictionary<string, AdapterReading> Adapters, List<AppReading>? Apps, string? Notice = null);

internal static class GlobalCollector
{
    // MIB_IF_ROW2 prefix through InterfaceAndOperStatusFlags. The system writes the full row.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct InterfaceRow
    {
        public ulong Luid;
        public uint Index;
        public Guid Guid;
        public fixed char Alias[257], Description[257];
        public uint AddressLength;
        public fixed byte Address[32], PermanentAddress[32];
        public uint Mtu, Type, TunnelType, MediaType, Medium, Access, Direction;
        public byte Flags;
        private fixed byte rest[512];
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetIfEntry2(ref InterfaceRow row);

    internal static Dictionary<string, AdapterReading> Adapters()
    {
        var result = new Dictionary<string, AdapterReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !Guid.TryParse(nic.Id, out var guid)) continue;
            // GetIfEntry2 accepts LUID or interface index.
            try
            {
                var props = nic.GetIPProperties();
                int index = nic.Supports(NetworkInterfaceComponent.IPv4) ? props.GetIPv4Properties().Index : props.GetIPv6Properties().Index;
                var row = new InterfaceRow { Index = (uint)index };
                if (GetIfEntry2(ref row) != 0 || (row.Flags & 1) == 0 || row.TunnelType != 0) continue;
                var stats = nic.GetIPStatistics();
                result[guid.ToString()] = new(nic.Name,new(stats.BytesSent,stats.BytesReceived));
            }
            catch (NetworkInformationException) { /* Adapter was detached during enumeration. */ }
        }
        return result;
    }

    public static async Task<GlobalSnapshot> Read(DateTimeOffset since, DateTimeOffset now, bool queryApps, CancellationToken token)
    {
        var adapters = Adapters();
        if (adapters.Count == 0) return new(adapters,null,"等待已连接的物理网卡。");
        if (!queryApps || now <= since) return new(adapters,null);
        var rows = new List<AppReading>();
        var seen = new HashSet<Guid>();
        string? notice = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                var id = profile.NetworkAdapter?.NetworkAdapterId;
                if (!id.HasValue || !adapters.TryGetValue(id.Value.ToString(),out var nic) ||
                    profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None || !seen.Add(id.Value)) continue;
                var reports = await profile.GetAttributedNetworkUsageAsync(since,now,new NetworkUsageStates()).AsTask(deadline.Token).ConfigureAwait(false);
                foreach (var report in reports)
                {
                    string raw = report.AttributionId ?? "";
                    string name = Path.GetFileName(raw.TrimEnd('\\'));
                    if (string.IsNullOrWhiteSpace(name)) name = report.AttributionName;
                    if (string.IsNullOrWhiteSpace(name)) name = Ledger.UnknownApp;
                    if (name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) name = name.ToLowerInvariant();
                    rows.Add(new($"{id}:{raw}:{name}",name,raw.Length == 0 ? "系统未返回应用标识" : raw,
                        nic.Name,new((long)report.BytesSent,(long)report.BytesReceived)));
                }
            }
            if (seen.Count == 0) notice = "网卡总量正在记录；Windows 尚未提供此网络的应用用量。";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { notice = "网卡总量正在记录；Windows 应用用量查询超时，将自动重试。"; }
        catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
        {
            notice = $"网卡总量正在记录；Windows 应用用量暂时不可用（0x{e.HResult:X8}）。";
        }
        return new(adapters,rows,notice);
    }
}
