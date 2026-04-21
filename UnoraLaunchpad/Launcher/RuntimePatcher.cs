using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace UnoraLaunchpad;

public sealed class RuntimePatcher(ClientVersion clientVersion, Stream stream, bool leaveOpen = false) : IDisposable
{
    private readonly BinaryWriter Writer = new(stream, Encoding.UTF8, leaveOpen);
    private bool IsDisposed;

    #region Patch Methods
    public void ApplyServerHostnamePatch(IPAddress ipAddress) => ApplyServerHostnamePatch(ipAddress.GetAddressBytes());

    public void ApplyFixDarknessPatch()
    {
        long addr = 0x5F1D0D;
        Console.WriteLine($"[Patch] Applying FixDarkness at 0x{addr:X}");
        stream.Position = addr;
        Writer.Write([0x08, 0x02]);
    }

    public void ApplyBytePatch(long address, byte[] bytes)
    {
        CheckIfDisposed();
        Console.WriteLine($"[Patch] Applying Bytes at 0x{address:X} (Len: {bytes.Length})");
        stream.Position = address;
        Writer.Write(bytes);
    }

    public void ApplyStringPatch(long address, string text)
    {
        CheckIfDisposed();
        Console.WriteLine($"[Patch] Applying String '{text}' at 0x{address:X}");
        stream.Position = address;
        Writer.Write(Encoding.ASCII.GetBytes(text + "\0"));
    }

    public void ApplyServerHostnamePatch(IEnumerable<byte> ipAddressBytes)
    {
        CheckIfDisposed();

        // 1. Write the PUSH <byte> for each IP byte (Original Unora logic)
        Console.WriteLine($"[Patch] Applying ServerHostname at 0x{clientVersion.ServerHostnamePatchAddress:X}");
        stream.Position = clientVersion.ServerHostnamePatchAddress;

        foreach (var ipByte in ipAddressBytes.Reverse())
        {
            Writer.Write((byte)0x6A); // PUSH
            Writer.Write(ipByte);
        }

        // 2. NOP out the original hostname resolution (Original Unora logic)
        Console.WriteLine($"[Patch] Applying SkipHostname (13 NOPs) at 0x{clientVersion.SkipHostnamePatchAddress:X}");
        stream.Position = clientVersion.SkipHostnamePatchAddress;

        for (var i = 0; i < 13; i++)
            Writer.Write((byte)0x90); // NOP
    }

    public void ApplyServerPortPatch(int port)
    {
        if (port <= 0)
            throw new ArgumentOutOfRangeException(nameof(port));

        CheckIfDisposed();

        Console.WriteLine($"[Patch] Applying ServerPort {port} at 0x{clientVersion.ServerPortPatchAddress:X}");
        stream.Position = clientVersion.ServerPortPatchAddress;

        var portHiByte = (port >> 8) & 0xFF;
        var portLoByte = port & 0xFF;

        Writer.Write((byte)portLoByte);
        Writer.Write((byte)portHiByte);
    }

    public void ApplySkipIntroVideoPatch()
    {
        CheckIfDisposed();

        Console.WriteLine($"[Patch] Applying SkipIntroVideo at 0x{clientVersion.IntroVideoPatchAddress:X}");
        stream.Position = clientVersion.IntroVideoPatchAddress;

        Writer.Write((byte)0x83); // CMP
        Writer.Write((byte)0xFA); // EDX
        Writer.Write((byte)0x00); // 0
        Writer.Write((byte)0x90); // NOP
        Writer.Write((byte)0x90); // NOP
        Writer.Write((byte)0x90); // NOP
    }

    public void ApplyMultipleInstancesPatch()
    {
        CheckIfDisposed();

        Console.WriteLine($"[Patch] Applying MultipleInstances at 0x{clientVersion.MultipleInstancePatchAddress:X}");
        stream.Position = clientVersion.MultipleInstancePatchAddress;

        Writer.Write((byte)0x31); // XOR
        Writer.Write((byte)0xC0); // EAX, EAX
        Writer.Write((byte)0x90); // NOP
        Writer.Write((byte)0x90); // NOP
        Writer.Write((byte)0x90); // NOP
        Writer.Write((byte)0x90); // NOP
    }

    public void ApplyHideWallsPatch()
    {
        CheckIfDisposed();

        Console.WriteLine($"[Patch] Applying HideWalls at 0x{clientVersion.HideWallsPatchAddress:X}");
        stream.Position = clientVersion.HideWallsPatchAddress;

        Writer.Write((byte)0xEB); // JMP SHORT
        Writer.Write((byte)0x17); // +17
        Writer.Write((byte)0x90); // NOP
    }
    #endregion

    #region IDisposable Methods
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool isDisposing)
    {
        if (IsDisposed)
            return;

        if (isDisposing)
            Writer.Dispose();

        IsDisposed = true;
    }

    private void CheckIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(GetType().Name);
    }
    #endregion
}
