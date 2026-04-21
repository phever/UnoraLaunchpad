using System;
using System.Runtime.InteropServices;

namespace UnoraLaunchpad.Linux;

internal static class LinuxNativeMethods
{
    public const int PTRACE_ATTACH = 16;
    public const int PTRACE_DETACH = 17;

    public const int SIGSTOP = 19;
    public const int SIGCONT = 18;

    [DllImport("libc", SetLastError = true)]
    public static extern long ptrace(int request, int pid, IntPtr addr, IntPtr data);

    [DllImport("libc", SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
}
