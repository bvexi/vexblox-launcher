using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VexBloxLauncher
{
    internal static class MemoryPatcher
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        private const uint ProcessAccess = 0x0438;
        private const uint MemCommit = 0x1000;
        private const uint MemMapped = 0x40000;
        private const uint PageNoAccess = 0x01;
        private const uint PageReadWrite = 0x04;
        private const uint PageWriteCopy = 0x08;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint PageExecuteWriteCopy = 0x80;
        private const uint PageGuard = 0x100;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, IntPtr size, uint protect, out uint old);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static readonly byte[] PemBegin = Encoding.ASCII.GetBytes("-----BEGIN PUBLIC KEY-----");
        private static readonly byte[] PemEnd = Encoding.ASCII.GetBytes("-----END PUBLIC KEY-----");

        public static void Run(Process process, string domain, string publicKeyPem, TimeSpan duration)
        {
            var replacements = new List<KeyValuePair<byte[], byte[]>>
            {
                new KeyValuePair<byte[], byte[]>(Encoding.ASCII.GetBytes("roblox.com"), Encoding.ASCII.GetBytes(domain)),
                new KeyValuePair<byte[], byte[]>(Encoding.Unicode.GetBytes("roblox.com"), Encoding.Unicode.GetBytes(domain)),
            };
            var key = publicKeyPem.Replace("\r\n", "\n").Trim();

            var handle = OpenProcess(ProcessAccess, false, process.Id);
            if (handle == IntPtr.Zero)
                return;

            var watch = Stopwatch.StartNew();
            var keyPatched = false;
            var passesAfterKey = 0;
            try
            {
                for (var pass = 0; watch.Elapsed < duration && !process.HasExited; pass++)
                {
                    foreach (var region in Regions(handle))
                    {
                        var data = new byte[(long)region.RegionSize];
                        if (!ReadProcessMemory(handle, region.BaseAddress, data, data.Length, out var read) || read == IntPtr.Zero)
                            continue;

                        foreach (var r in replacements)
                            foreach (var offset in Find(data, r.Key))
                                Patch(handle, region, offset, r.Value);

                        foreach (var offset in Find(data, PemBegin))
                        {
                            var end = IndexOf(data, PemEnd, offset);
                            if (end < 0)
                                continue;
                            var length = end + PemEnd.Length - offset;
                            if (length < 400)
                                continue;
                            var current = Encoding.ASCII.GetString(data, offset, length);
                            var crlf = current.Contains("\r\n");
                            if (current.Replace("\r\n", "\n") == key)
                                continue;
                            var replacement = Encoding.ASCII.GetBytes(crlf ? key.Replace("\n", "\r\n") : key);
                            if (replacement.Length == length && Patch(handle, region, offset, replacement))
                                keyPatched = true;
                        }
                    }
                    if (keyPatched && ++passesAfterKey >= 5)
                        break;
                    Thread.Sleep(15);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static IEnumerable<MemoryBasicInformation> Regions(IntPtr handle)
        {
            long address = 0x10000;
            while (address < 0x7FFF0000)
            {
                if (VirtualQueryEx(handle, new IntPtr(address), out var info, Marshal.SizeOf(typeof(MemoryBasicInformation))) == 0)
                    yield break;
                var size = (long)info.RegionSize;
                if (size <= 0)
                    yield break;
                if (info.State == MemCommit && info.Type != MemMapped && (info.Protect & (PageGuard | PageNoAccess)) == 0 && size < 256L * 1024 * 1024)
                    yield return info;
                address = (long)info.BaseAddress + size;
            }
        }

        private static bool Patch(IntPtr handle, MemoryBasicInformation region, int offset, byte[] bytes)
        {
            var address = new IntPtr((long)region.BaseAddress + offset);
            var writable = (region.Protect & (PageReadWrite | PageWriteCopy | PageExecuteReadWrite | PageExecuteWriteCopy)) != 0;
            uint old = 0;
            if (!writable && !VirtualProtectEx(handle, address, new IntPtr(bytes.Length), PageReadWrite, out old))
                return false;
            var ok = WriteProcessMemory(handle, address, bytes, bytes.Length, out _);
            if (!writable)
                VirtualProtectEx(handle, address, new IntPtr(bytes.Length), old, out _);
            return ok;
        }

        private static IEnumerable<int> Find(byte[] data, byte[] pattern)
        {
            var i = 0;
            while ((i = IndexOf(data, pattern, i)) >= 0)
            {
                yield return i;
                i += pattern.Length;
            }
        }

        private static int IndexOf(byte[] data, byte[] pattern, int start)
        {
            var first = pattern[0];
            var last = data.Length - pattern.Length;
            for (var i = start; i <= last; i++)
            {
                i = Array.IndexOf(data, first, i, last - i + 1);
                if (i < 0)
                    return -1;
                var j = 1;
                while (j < pattern.Length && data[i + j] == pattern[j])
                    j++;
                if (j == pattern.Length)
                    return i;
            }
            return -1;
        }
    }
}
