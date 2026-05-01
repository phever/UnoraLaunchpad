using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace UnoraLaunchpad;

public sealed class LegacyDarkAgesLauncher : IGameLauncher
{
    public Task<Process> LaunchAsync(LaunchContext context)
    {
        var exePath = Path.Combine(context.InstallRoot, "Unora.exe");
        var ipAddress = ResolveHostname(context.LobbyHost);

        int pid;
        using (var suspended = SuspendedProcess.Start(exePath))
        {
            pid = suspended.ProcessId;
            ApplyPatches(suspended, ipAddress, context.LobbyPort, context.SkipIntro);

            if (context.UseDawndWindower)
            {
                var handle = NativeMethods.OpenProcess(ProcessAccessFlags.FullAccess, true, pid);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        InjectDll(handle, "dawnd.dll");
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(handle);
                    }
                }
            }
        } // suspended.Dispose() resumes the main thread

        return Task.FromResult(Process.GetProcessById(pid));
    }

    private static void ApplyPatches(SuspendedProcess process, IPAddress serverIp, int serverPort, bool skipIntro)
    {
        using var stream = new ProcessMemoryStream(process.ProcessId);
        using var patcher = new RuntimePatcher(ClientVersion.Version741, stream, leaveOpen: true);

        patcher.ApplyServerHostnamePatch(serverIp);
        patcher.ApplyServerPortPatch(serverPort);
        patcher.ApplyFixDarknessPatch();

        if (skipIntro)
            patcher.ApplySkipIntroVideoPatch();

        patcher.ApplyMultipleInstancesPatch();
    }

    private static IPAddress ResolveHostname(string hostname)
    {
        var hostEntry = Dns.GetHostEntry(hostname);
        var ipv4 = from ip in hostEntry.AddressList
                   where ip.AddressFamily == AddressFamily.InterNetwork
                   select ip;
        return ipv4.FirstOrDefault();
    }

    private static void InjectDll(IntPtr accessHandle, string dllName)
    {
        var nameLength = dllName.Length + 1;
        var allocate = NativeMethods.VirtualAllocEx(
            accessHandle, IntPtr.Zero, (IntPtr)nameLength, 0x1000, 0x40);

        try
        {
            NativeMethods.WriteProcessMemory(
                accessHandle, allocate, dllName, (UIntPtr)nameLength, out _);

            var injectionPtr = NativeMethods.GetProcAddress(
                NativeMethods.GetModuleHandle("kernel32.dll"), "LoadLibraryA");

            if (injectionPtr == UIntPtr.Zero)
                throw new InvalidOperationException("Injection pointer was null.");

            var thread = NativeMethods.CreateRemoteThread(
                accessHandle, IntPtr.Zero, IntPtr.Zero, injectionPtr, allocate, 0, out _);

            if (thread == IntPtr.Zero)
                throw new InvalidOperationException("Remote injection thread was null. Try again...");

            try
            {
                var result = NativeMethods.WaitForSingleObject(thread, 10 * 1000);
                if (result != WaitEventResult.Signaled)
                    throw new InvalidOperationException("Injection thread timed out, or signaled incorrectly. Try again...");
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }
        }
        finally
        {
            NativeMethods.VirtualFreeEx(accessHandle, allocate, (UIntPtr)0, 0x8000);
        }
    }
}
