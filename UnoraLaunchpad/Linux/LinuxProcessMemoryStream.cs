using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UnoraLaunchpad.Linux;

public sealed class LinuxProcessMemoryStream : Stream
{
    private const long DEFAULT_BASE_ADDRESS = 0x400000;
    private readonly int _pid;
    private readonly FileStream _memStream;
    private readonly long _actualBaseAddress;
    private bool _isDisposed;
    private bool _attached;
    private long _position = DEFAULT_BASE_ADDRESS;

    public LinuxProcessMemoryStream(int pid)
    {
        _pid = pid;
        _actualBaseAddress = GetModuleBaseAddress(pid);

        if (_actualBaseAddress != DEFAULT_BASE_ADDRESS)
        {
            Console.WriteLine($"[Memory] Detected relocation: {DEFAULT_BASE_ADDRESS:X} -> {_actualBaseAddress:X}");
        }

        string memPath = $"/proc/{pid}/mem";
        if (!File.Exists(memPath))
        {
            throw new FileNotFoundException($"Process memory file not found: {memPath}");
        }

        try
        {
            // Try opening with ReadWrite first. This works without ptrace if ptrace_scope=0
            _memStream = new FileStream(memPath, FileMode.Open, FileAccess.ReadWrite);
        }
        catch (UnauthorizedAccessException)
        {
            // Permission denied, try to attach via ptrace
            if (Attach(pid))
            {
                _attached = true;
                _memStream = new FileStream(memPath, FileMode.Open, FileAccess.ReadWrite);
            }
            else
            {
                // If attach fails, try opening as read-only as a last resort (might still work for some patches)
                _memStream = new FileStream(memPath, FileMode.Open, FileAccess.Read);
            }
        }
    }

    private long GetModuleBaseAddress(int pid)
    {
        string mapsPath = $"/proc/{pid}/maps";
        if (!File.Exists(mapsPath)) return DEFAULT_BASE_ADDRESS;

        try
        {
            // We look for the first executable mapping of the game
            foreach (var line in File.ReadLines(mapsPath))
            {
                var lineLower = line.ToLower();
                if (lineLower.Contains("darkages.exe") || lineLower.Contains("unora.exe"))
                {
                    var parts = line.Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out long addr))
                    {
                        return addr;
                    }
                }
            }
        }
        catch { }

        return DEFAULT_BASE_ADDRESS;
    }

    private bool Attach(int pid)
    {
        long result = LinuxNativeMethods.ptrace(LinuxNativeMethods.PTRACE_ATTACH, pid, IntPtr.Zero, IntPtr.Zero);
        if (result == -1)
        {
            return false;
        }

        // Wait for the process to stop
        LinuxNativeMethods.waitpid(pid, out _, 0);
        return true;
    }

    private long GetPhysicalAddress(long virtualAddress)
    {
        // Adjust for relocation if necessary
        return _actualBaseAddress + (virtualAddress - DEFAULT_BASE_ADDRESS);
    }

    private void Detach()
    {
        if (_attached)
        {
            LinuxNativeMethods.ptrace(LinuxNativeMethods.PTRACE_DETACH, _pid, IntPtr.Zero, IntPtr.Zero);
            _attached = false;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _memStream.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override void Flush() => _memStream.Flush();

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin: _position = offset; break;
            case SeekOrigin.Current: _position += offset; break;
            case SeekOrigin.End: throw new NotSupportedException();
        }
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        _memStream.Seek(GetPhysicalAddress(_position), SeekOrigin.Begin);
        int read = _memStream.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!CanWrite)
        {
            throw new UnauthorizedAccessException($"Stream is not writable for PID {_pid}. Try: echo 0 | sudo tee /proc/sys/kernel/yama/ptrace_scope");
        }

        _memStream.Seek(GetPhysicalAddress(_position), SeekOrigin.Begin);
        _memStream.Write(buffer, offset, count);
        _memStream.Flush();
        _position += count;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _memStream?.Dispose();
            }

            Detach();
            _isDisposed = true;
        }
        base.Dispose(disposing);
    }
}
