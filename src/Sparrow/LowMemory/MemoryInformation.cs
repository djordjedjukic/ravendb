﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Platform.Posix;
using Sparrow.Platform.Posix.macOS;
using Sparrow.Platform.Win32;
using Sparrow.Utils;

namespace Sparrow.LowMemory
{
    public static class MemoryInformation
    {
        private static readonly Logger Logger = LoggingSource.Instance.GetLogger<MemoryInfoResult>("Raven/Server");

        private static readonly ConcurrentQueue<Tuple<long, DateTime>> MemByTime = new ConcurrentQueue<Tuple<long, DateTime>>();
        private static DateTime _memoryRecordsSet;
        private static readonly TimeSpan MemByTimeThrottleTime = TimeSpan.FromMilliseconds(100);

        private static readonly byte[] VmRss = Encoding.UTF8.GetBytes("VmRSS:");
        private static readonly byte[] MemAvailable = Encoding.UTF8.GetBytes("MemAvailable:");
        private static readonly byte[] MemFree = Encoding.UTF8.GetBytes("MemFree:");
        private static readonly byte[] MemTotal = Encoding.UTF8.GetBytes("MemTotal:");
        private static readonly byte[] SwapTotal = Encoding.UTF8.GetBytes("SwapTotal:");
        private static readonly byte[] Committed_AS = Encoding.UTF8.GetBytes("Committed_AS:");

        private const string CgroupMemoryLimit = "/sys/fs/cgroup/memory/memory.limit_in_bytes";
        private const string CgroupMaxMemoryUsage = "/sys/fs/cgroup/memory/memory.max_usage_in_bytes";
        private const string CgroupMemoryUsage = "/sys/fs/cgroup/memory/memory.usage_in_bytes";

        public static long HighLastOneMinute;
        public static long LowLastOneMinute = long.MaxValue;
        public static long HighLastFiveMinutes;
        public static long LowLastFiveMinutes = long.MaxValue;
        public static long HighSinceStartup;
        public static long LowSinceStartup = long.MaxValue;


        private static bool _failedToGetAvailablePhysicalMemory;
        private static readonly MemoryInfoResult FailedResult = new MemoryInfoResult
        {
            AvailableMemory = new Size(256, SizeUnit.Megabytes),
            TotalPhysicalMemory = new Size(256, SizeUnit.Megabytes),
            TotalCommittableMemory = new Size(384, SizeUnit.Megabytes),// also include "page file"
            CurrentCommitCharge = new Size(256, SizeUnit.Megabytes),
            InstalledMemory = new Size(256, SizeUnit.Megabytes),
            MemoryUsageRecords =
            new MemoryInfoResult.MemoryUsageLowHigh
            {
                High = new MemoryInfoResult.MemoryUsageIntervals
                {
                    LastFiveMinutes = new Size(0, SizeUnit.Bytes),
                    LastOneMinute = new Size(0, SizeUnit.Bytes),
                    SinceStartup = new Size(0, SizeUnit.Bytes)
                },
                Low = new MemoryInfoResult.MemoryUsageIntervals
                {
                    LastFiveMinutes = new Size(0, SizeUnit.Bytes),
                    LastOneMinute = new Size(0, SizeUnit.Bytes),
                    SinceStartup = new Size(0, SizeUnit.Bytes)
                }
            }
        };

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        public static bool DisableEarlyOutOfMemoryCheck =
            string.Equals(Environment.GetEnvironmentVariable("RAVEN_DISABLE_EARLY_OOM"), "true", StringComparison.OrdinalIgnoreCase);

        public static bool EnableEarlyOutOfMemoryCheck =
           string.Equals(Environment.GetEnvironmentVariable("RAVEN_ENABLE_EARLY_OOM"), "true", StringComparison.OrdinalIgnoreCase);

        public static bool EnableEarlyOutOfMemoryChecks = false; // we don't want this to run on the clients

        public static void AssertNotAboutToRunOutOfMemory(float minimumFreeCommittedMemory)
        {
            if (EnableEarlyOutOfMemoryChecks == false)
                return;

            if (DisableEarlyOutOfMemoryCheck)
                return;

            if (PlatformDetails.RunningOnPosix &&       // we only _need_ this check on Windows
                EnableEarlyOutOfMemoryCheck == false)   // but we want to enable this manually if needed
                return;

            // if we are about to create a new thread, might not always be a good idea:
            // https://ayende.com/blog/181537-B/production-test-run-overburdened-and-under-provisioned
            // https://ayende.com/blog/181569-A/threadpool-vs-pool-thread

            var memInfo = GetMemoryInfo();
            Size overage;
            if (memInfo.CurrentCommitCharge > memInfo.TotalCommittableMemory)
            {
                // this can happen on containers, since we get this information from the host, and
                // sometimes this kind of stat is shared, see: 
                // https://fabiokung.com/2014/03/13/memory-inside-linux-containers/

                overage =
                    (memInfo.TotalPhysicalMemory * minimumFreeCommittedMemory) +  //extra to keep free
                    (memInfo.TotalPhysicalMemory - memInfo.AvailableMemory);      //actually in use now
                if (overage >= memInfo.TotalPhysicalMemory)
                {
                    ThrowInsufficentMemory(memInfo);
                    return;
                }

                return;
            }

            overage = (memInfo.TotalCommittableMemory * minimumFreeCommittedMemory) + memInfo.CurrentCommitCharge;
            if (overage >= memInfo.TotalCommittableMemory)
            {
                ThrowInsufficentMemory(memInfo);
            }
        }

        private static void ThrowInsufficentMemory(MemoryInfoResult memInfo)
        {
            throw new EarlyOutOfMemoryException($"The amount of available memory to commit on the system is low. Commit charge: {memInfo.CurrentCommitCharge} / {memInfo.TotalCommittableMemory}. Memory: {memInfo.TotalPhysicalMemory - memInfo.AvailableMemory} / {memInfo.TotalPhysicalMemory}");
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern unsafe bool GlobalMemoryStatusEx(MemoryStatusEx* lpBuffer);

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKb);

        public static long GetRssMemoryUsage(int processId)
        {
            var path = $"/proc/{processId}/status";

            try
            {
                using (var bufferedReader = new KernelVirtualFileSystemUtils.BufferedPosixKeyValueOutputValueReader(path))
                {
                    bufferedReader.ReadFileIntoBuffer();
                    var vmrss = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(VmRss);
                    return vmrss * 1024;// value is in KB, we need to return bytes
                }
            }
            catch (Exception ex)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to read value from {path}", ex);
                return -1;
            }
        }

        public static (long MemAvailable, long SwapTotal, long Commited, long TotalMemory) GetFromProcMemInfo()
        {
            const string path = "/proc/meminfo";

            // this is different then sysinfo freeram+buffered (and the closest to the real free memory)
            // MemFree is really different then MemAvailable (while free is usually lower then the real free,
            // and available is only estimated free which sometimes higher then the real free memory)
            // for some distros we have only MemFree
            try
            {
                using (var bufferedReader = new KernelVirtualFileSystemUtils.BufferedPosixKeyValueOutputValueReader(path))
                {
                    bufferedReader.ReadFileIntoBuffer();
                    var memAvailable = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(MemAvailable);
                    var memFree = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(MemFree);
                    var swapTotal = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(SwapTotal);
                    var commited = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(Committed_AS);
                    var total = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(MemTotal);
                    return (
                        MemAvailable: Math.Max(memAvailable, memFree) * 1024,
                        SwapTotal: swapTotal * 1024,
                        Commited: commited * 1024,
                        TotalMemory: total * 1024
                    );
                }
            }
            catch (Exception ex)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to read value from {path}", ex);

                return (-1, -1, -1, -1);
            }
        }

        public static (double InstalledMemory, double UsableMemory) GetMemoryInfoInGb()
        {
            var memoryInformation = GetMemoryInfo();
            var installedMemoryInGb = memoryInformation.InstalledMemory.GetDoubleValue(SizeUnit.Gigabytes);
            var usableMemoryInGb = memoryInformation.TotalPhysicalMemory.GetDoubleValue(SizeUnit.Gigabytes);
            return (installedMemoryInGb, usableMemoryInGb);
        }

        public static MemoryInfoResult GetMemoryInfo()
        {
            if (_failedToGetAvailablePhysicalMemory)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Because of a previous error in getting available memory, we are now lying and saying we have 256MB free");
                return FailedResult;
            }

            try
            {
                if (PlatformDetails.RunningOnPosix == false)
                    return GetMemoryInfoWindows();

                if (PlatformDetails.RunningOnMacOsx)
                    return GetMemoryInfoMacOs();

                return GetMemoryInfoLinux();
            }
            catch (Exception e)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Error while trying to get available memory, will stop trying and report that there is 256MB free only from now on", e);
                _failedToGetAvailablePhysicalMemory = true;
                return FailedResult;
            }
        }

        private static MemoryInfoResult GetMemoryInfoLinux()
        {
            var fromProcMemInfo = GetFromProcMemInfo();
            var totalPhysicalMemoryInBytes = fromProcMemInfo.TotalMemory;
            var availableRamInBytes = fromProcMemInfo.MemAvailable;
            var commitedMemoryInBytes = fromProcMemInfo.Commited;

            // On Linux, we use the swap + ram as the commit limit, because the actual limit
            // is dependent on many different factors
            var commitLimitInBytes = totalPhysicalMemoryInBytes + fromProcMemInfo.SwapTotal;

            var cgroupMemoryLimit = KernelVirtualFileSystemUtils.ReadNumberFromCgroupFile(CgroupMemoryLimit);
            var cgroupMaxMemoryUsage = KernelVirtualFileSystemUtils.ReadNumberFromCgroupFile(CgroupMaxMemoryUsage);
            // here we need to deal with _soft_ limit, so we'll take the largest of these values
            var maxMemoryUsage = Math.Max(cgroupMemoryLimit ?? 0, cgroupMaxMemoryUsage ?? 0);

            if (maxMemoryUsage != 0 && maxMemoryUsage <= totalPhysicalMemoryInBytes)
            {
                // running in a limitted cgroup
                var cgroupMemoryUsage = KernelVirtualFileSystemUtils.ReadNumberFromCgroupFile(CgroupMemoryUsage);
                if (cgroupMemoryUsage != null)
                {
                    commitedMemoryInBytes = cgroupMemoryUsage.Value;
                    availableRamInBytes = maxMemoryUsage - cgroupMemoryUsage.Value;
                }

                totalPhysicalMemoryInBytes = maxMemoryUsage;
                commitLimitInBytes = Math.Max(maxMemoryUsage, commitedMemoryInBytes);
            }

            return BuildPosixMemoryInfoResult(
                availableRamInBytes,
                totalPhysicalMemoryInBytes,
                commitedMemoryInBytes, 
                commitLimitInBytes);
        }

        private static MemoryInfoResult BuildPosixMemoryInfoResult(long availableRamInBytes, long totalPhysicalMemoryInBytes, long commitedMemoryInBytes, long commitLimitInBytes)
        {
            SetMemoryRecords(availableRamInBytes);

            var totalPhysicalMemory = new Size(totalPhysicalMemoryInBytes, SizeUnit.Bytes);
            return new MemoryInfoResult
            {
                TotalCommittableMemory = new Size(commitLimitInBytes, SizeUnit.Bytes),
                CurrentCommitCharge = new Size(commitedMemoryInBytes, SizeUnit.Bytes),

                AvailableMemory = new Size(availableRamInBytes, SizeUnit.Bytes),
                TotalPhysicalMemory = totalPhysicalMemory,
                InstalledMemory = totalPhysicalMemory,
                MemoryUsageRecords = new MemoryInfoResult.MemoryUsageLowHigh
                {
                    High = new MemoryInfoResult.MemoryUsageIntervals
                    {
                        LastOneMinute = new Size(HighLastOneMinute, SizeUnit.Bytes),
                        LastFiveMinutes = new Size(HighLastFiveMinutes, SizeUnit.Bytes),
                        SinceStartup = new Size(HighSinceStartup, SizeUnit.Bytes)
                    },
                    Low = new MemoryInfoResult.MemoryUsageIntervals
                    {
                        LastOneMinute = new Size(LowLastOneMinute, SizeUnit.Bytes),
                        LastFiveMinutes = new Size(LowLastFiveMinutes, SizeUnit.Bytes),
                        SinceStartup = new Size(LowSinceStartup, SizeUnit.Bytes)
                    }
                }
            };
        }

        private static unsafe MemoryInfoResult GetMemoryInfoMacOs()
        {
            var mib = new[] { (int)TopLevelIdentifiers.CTL_HW, (int)CtkHwIdentifiers.HW_MEMSIZE };
            ulong physicalMemory = 0;
            var len = sizeof(ulong);

            if (macSyscall.sysctl(mib, 2, &physicalMemory, &len, null, UIntPtr.Zero) != 0)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Failure when trying to read physical memory info from MacOS, error code was: " + Marshal.GetLastWin32Error());
                return FailedResult;
            }

            var totalPhysicalMemoryInBytes = (long)physicalMemory;

            uint pageSize;
            var vmStats = new vm_statistics64();

            var machPort = macSyscall.mach_host_self();
            var count = sizeof(vm_statistics64) / sizeof(uint);

            if (macSyscall.host_page_size(machPort, &pageSize) != 0 ||
                macSyscall.host_statistics64(machPort, (int)Flavor.HOST_VM_INFO64, &vmStats, &count) != 0)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Failure when trying to get vm_stats from MacOS, error code was: " + Marshal.GetLastWin32Error());
                return FailedResult;
            }

            // swap usage
            var swapu = new xsw_usage();
            len = sizeof(xsw_usage);
            mib = new[] { (int)TopLevelIdentifiers.CTL_VM, (int)CtlVmIdentifiers.VM_SWAPUSAGE };
            if (macSyscall.sysctl(mib, 2, &swapu, &len, null, UIntPtr.Zero) != 0)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Failure when trying to read swap info from MacOS, error code was: " + Marshal.GetLastWin32Error());
                return FailedResult;
            }

            /* Free memory: This is RAM that's not being used.
             * Wired memory: Information in this memory can't be moved to the hard disk, so it must stay in RAM. The amount of Wired memory depends on the applications you are using.
             * Active memory: This information is currently in memory, and has been recently used.
             * Inactive memory: This information in memory is not actively being used, but was recently used. */
            var availableRamInBytes = (vmStats.FreePagesCount + vmStats.InactivePagesCount) * pageSize;

            // there is no commited memory value in OSX,
            // this is an approximation: wired + active + swap used
            var commitedMemoryInBytes = vmStats.WirePagesCount + vmStats.ActivePagesCount * pageSize + (long)swapu.xsu_used;

            // commit limit: physical memory + swap
            var commitLimitInBytes = (long)(physicalMemory + swapu.xsu_total);

            return BuildPosixMemoryInfoResult(availableRamInBytes, totalPhysicalMemoryInBytes, commitedMemoryInBytes, commitLimitInBytes);
        }

        private static unsafe MemoryInfoResult GetMemoryInfoWindows()
        {
            // windows
            var memoryStatus = new MemoryStatusEx
            {
                dwLength = (uint)sizeof(MemoryStatusEx)
            };

            if (GlobalMemoryStatusEx(&memoryStatus) == false)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Failure when trying to read memory info from Windows, error code is: " + Marshal.GetLastWin32Error());
                return FailedResult;
            }

            // The amount of physical memory retrieved by the GetPhysicallyInstalledSystemMemory function 
            // must be equal to or greater than the amount reported by the GlobalMemoryStatusEx function
            // if it is less, the SMBIOS data is malformed and the function fails with ERROR_INVALID_DATA. 
            // Malformed SMBIOS data may indicate a problem with the user's computer.
            var fetchedInstalledMemory = GetPhysicallyInstalledSystemMemory(out var installedMemoryInKb);

            SetMemoryRecords((long)memoryStatus.ullAvailPhys);

            return new MemoryInfoResult
            {
                TotalCommittableMemory = new Size((long)memoryStatus.ullTotalPageFile, SizeUnit.Bytes),
                CurrentCommitCharge = new Size((long)(memoryStatus.ullTotalPageFile - memoryStatus.ullAvailPageFile), SizeUnit.Bytes),
                AvailableMemory = new Size((long)memoryStatus.ullAvailPhys, SizeUnit.Bytes),
                TotalPhysicalMemory = new Size((long)memoryStatus.ullTotalPhys, SizeUnit.Bytes),
                InstalledMemory = fetchedInstalledMemory ?
                    new Size(installedMemoryInKb, SizeUnit.Kilobytes) :
                    new Size((long)memoryStatus.ullTotalPhys, SizeUnit.Bytes),
                MemoryUsageRecords = new MemoryInfoResult.MemoryUsageLowHigh
                {
                    High = new MemoryInfoResult.MemoryUsageIntervals
                    {
                        LastOneMinute = new Size(HighLastOneMinute, SizeUnit.Bytes),
                        LastFiveMinutes = new Size(HighLastFiveMinutes, SizeUnit.Bytes),
                        SinceStartup = new Size(HighSinceStartup, SizeUnit.Bytes)
                    },
                    Low = new MemoryInfoResult.MemoryUsageIntervals
                    {
                        LastOneMinute = new Size(LowLastOneMinute, SizeUnit.Bytes),
                        LastFiveMinutes = new Size(LowLastFiveMinutes, SizeUnit.Bytes),
                        SinceStartup = new Size(LowSinceStartup, SizeUnit.Bytes)
                    }
                }
            };
        }

        public static (long WorkingSet, long TotalUnmanagedAllocations, long ManagedMemory, long MappedTemp) MemoryStats()
        {
            using (var currentProcess = Process.GetCurrentProcess())
            {
                var workingSet = PlatformDetails.RunningOnLinux == false
                        ? currentProcess.WorkingSet64
                        : GetRssMemoryUsage(currentProcess.Id);

                long totalUnmanagedAllocations = 0;
                foreach (var stats in NativeMemory.ThreadAllocations.Values)
                {
                    if (stats == null)
                        continue;
                    if (stats.IsThreadAlive())
                        totalUnmanagedAllocations += stats.TotalAllocated;
                }

                // scratch buffers, compression buffers
                var totalMappedTemp = 0L;
                foreach (var mapping in NativeMemory.FileMapping)
                {
                    if (mapping.Key == null)
                        continue;

                    if (mapping.Key.EndsWith(".buffers", StringComparison.OrdinalIgnoreCase) == false)
                        continue;

                    var maxMapped = 0L;
                    foreach (var singleMapping in mapping.Value)
                    {
                        maxMapped = Math.Max(maxMapped, singleMapping.Value);
                    }

                    totalMappedTemp += maxMapped;
                }

                var managedMemory = GC.GetTotalMemory(false);
                return (workingSet, totalUnmanagedAllocations, managedMemory, totalMappedTemp);
            }
        }

        private static void SetMemoryRecords(long availableRamInBytes)
        {
            var now = DateTime.UtcNow;

            if (HighSinceStartup < availableRamInBytes)
                HighSinceStartup = availableRamInBytes;
            if (LowSinceStartup > availableRamInBytes)
                LowSinceStartup = availableRamInBytes;

            while (MemByTime.TryPeek(out var existing) &&
                (now - existing.Item2) > TimeSpan.FromMinutes(5))
            {
                if (MemByTime.TryDequeue(out _) == false)
                    break;
            }

            if (now - _memoryRecordsSet < MemByTimeThrottleTime)
                return;

            _memoryRecordsSet = now;

            MemByTime.Enqueue(new Tuple<long, DateTime>(availableRamInBytes, now));

            long highLastOneMinute = 0;
            long lowLastOneMinute = long.MaxValue;
            long highLastFiveMinutes = 0;
            long lowLastFiveMinutes = long.MaxValue;

            foreach (var item in MemByTime)
            {
                if (now - item.Item2 < TimeSpan.FromMinutes(1))
                {
                    if (highLastOneMinute < item.Item1)
                        highLastOneMinute = item.Item1;
                    if (lowLastOneMinute > item.Item1)
                        lowLastOneMinute = item.Item1;
                }
                if (highLastFiveMinutes < item.Item1)
                    highLastFiveMinutes = item.Item1;
                if (lowLastFiveMinutes > item.Item1)
                    lowLastFiveMinutes = item.Item1;
            }

            HighLastOneMinute = highLastOneMinute;
            LowLastOneMinute = lowLastOneMinute;
            HighLastFiveMinutes = highLastFiveMinutes;
            LowLastFiveMinutes = lowLastFiveMinutes;
        }

        public static string IsSwappingOnHddInsteadOfSsd()
        {
            if (PlatformDetails.RunningOnPosix)
                return CheckPageFileOnHdd.PosixIsSwappingOnHddInsteadOfSsd();
            return CheckPageFileOnHdd.WindowsIsSwappingOnHddInsteadOfSsd();
        }

        public static unsafe bool WillCauseHardPageFault(byte* addr, long length) => PlatformDetails.RunningOnPosix ? PosixMemoryQueryMethods.WillCauseHardPageFault(addr, length) : Win32MemoryQueryMethods.WillCauseHardPageFault(addr, length);
    }

    public struct MemoryInfoResult
    {
        public class MemoryUsageIntervals
        {
            public Size LastOneMinute;
            public Size LastFiveMinutes;
            public Size SinceStartup;
        }
        public class MemoryUsageLowHigh
        {
            public MemoryUsageIntervals High;
            public MemoryUsageIntervals Low;
        }

        public Size TotalCommittableMemory;
        public Size CurrentCommitCharge;

        public Size TotalPhysicalMemory;
        public Size InstalledMemory;
        public Size AvailableMemory;
        public MemoryUsageLowHigh MemoryUsageRecords;
    }


    public class EarlyOutOfMemoryException : Exception
    {
        public EarlyOutOfMemoryException() { }
        public EarlyOutOfMemoryException(string message) : base(message) { }
        public EarlyOutOfMemoryException(string message, Exception inner) : base(message, inner) { }
    }

}
