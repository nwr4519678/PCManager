using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PCManager.Core.Services;

public class TcpProcessRecord
{
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public string State { get; set; } = "";
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
}

public static class NetworkMonitoringService
{
    private const int AF_INET = 2; // IPv4
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] localPort;
        public uint remoteAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] remotePort;
        public uint owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, 
        int ipVersion, int tblClass, uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort,
        int ipVersion, int tblClass, uint reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] localPort;
        public uint owningPid;
    }

    private const int UDP_TABLE_OWNER_PID = 1;

    public static Dictionary<int, List<int>> GetListeningPortsByProcess()
    {
        var map = new Dictionary<int, List<int>>();
        
        // --- Get TCP Listeners ---
        int tcpBufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref tcpBufferSize, true, AF_INET, TCP_TABLE_OWNER_PID_ALL);
        IntPtr tcpBuffer = Marshal.AllocHGlobal(tcpBufferSize);
        try
        {
            if (GetExtendedTcpTable(tcpBuffer, ref tcpBufferSize, true, AF_INET, TCP_TABLE_OWNER_PID_ALL) == 0)
            {
                int rows = Marshal.ReadInt32(tcpBuffer);
                IntPtr ptr = IntPtr.Add(tcpBuffer, 4);
                for (int i = 0; i < rows; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                    if (row.state == 2) // MIB_TCP_STATE_LISTEN
                    {
                        int port = (row.localPort[0] << 8) + row.localPort[1];
                        int pid = (int)row.owningPid;
                        if (!map.ContainsKey(pid)) map[pid] = new List<int>();
                        if (!map[pid].Contains(port)) map[pid].Add(port);
                    }
                    ptr = IntPtr.Add(ptr, Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID)));
                }
            }
        }
        finally { Marshal.FreeHGlobal(tcpBuffer); }

        // --- Get UDP Listeners ---
        int udpBufferSize = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref udpBufferSize, true, AF_INET, UDP_TABLE_OWNER_PID);
        IntPtr udpBuffer = Marshal.AllocHGlobal(udpBufferSize);
        try
        {
            if (GetExtendedUdpTable(udpBuffer, ref udpBufferSize, true, AF_INET, UDP_TABLE_OWNER_PID) == 0)
            {
                int rows = Marshal.ReadInt32(udpBuffer);
                IntPtr ptr = IntPtr.Add(udpBuffer, 4);
                for (int i = 0; i < rows; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(ptr);
                    int port = (row.localPort[0] << 8) + row.localPort[1];
                    int pid = (int)row.owningPid;
                    if (!map.ContainsKey(pid)) map[pid] = new List<int>();
                    if (!map[pid].Contains(port)) map[pid].Add(port);
                    ptr = IntPtr.Add(ptr, Marshal.SizeOf(typeof(MIB_UDPROW_OWNER_PID)));
                }
            }
        }
        finally { Marshal.FreeHGlobal(udpBuffer); }

        return map;
    }

    public static List<TcpProcessRecord> GetActiveTcpConnections()
    {
        var result = new List<TcpProcessRecord>();
        int bufferSize = 0;
        uint res = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, AF_INET, TCP_TABLE_OWNER_PID_ALL);

        if (res != 0 && res != 122) // 122 = ERROR_INSUFFICIENT_BUFFER
            return result;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            res = GetExtendedTcpTable(buffer, ref bufferSize, true, AF_INET, TCP_TABLE_OWNER_PID_ALL);
            if (res == 0)
            {
                int rows = Marshal.ReadInt32(buffer);
                IntPtr ptr = IntPtr.Add(buffer, 4);

                for (int i = 0; i < rows; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                    
                    var stateStr = row.state switch
                    {
                        1 => "Closed",
                        2 => "Listen",
                        3 => "Syn-Sent",
                        4 => "Syn-Received",
                        5 => "Established",
                        6 => "Fin-Wait-1",
                        7 => "Fin-Wait-2",
                        8 => "Close-Wait",
                        9 => "Closing",
                        10 => "Last-Ack",
                        11 => "Time-Wait",
                        12 => "Delete-TCB",
                        _ => "Unknown"
                    };

                    string localIp = new IPAddress(row.localAddr).ToString();
                    string remoteIp = new IPAddress(row.remoteAddr).ToString();
                    int localPort = (row.localPort[0] << 8) + row.localPort[1];
                    int remotePort = (row.remotePort[0] << 8) + row.remotePort[1];

                    if (localIp != "127.0.0.1" && remoteIp != "127.0.0.1" && remoteIp != "0.0.0.0")
                    {
                        string processName;
                        try 
                        {
                            var proc = Process.GetProcessById((int)row.owningPid);
                            processName = proc.ProcessName;
                        } 
                        catch 
                        { 
                            processName = "System Access Denied"; 
                        }

                        result.Add(new TcpProcessRecord
                        {
                            LocalAddress = localIp,
                            LocalPort = localPort,
                            RemoteAddress = remoteIp,
                            RemotePort = remotePort,
                            State = stateStr,
                            ProcessId = (int)row.owningPid,
                            ProcessName = processName
                        });
                    }

                    ptr = IntPtr.Add(ptr, Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID)));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }
}
