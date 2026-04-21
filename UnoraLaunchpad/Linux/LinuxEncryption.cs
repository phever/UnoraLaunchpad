using System;
using System.Text;

namespace UnoraLaunchpad.Linux;

/// <summary>
/// Linux-specific encryption implementation to avoid dependencies on Windows-only ProtectedData.
/// </summary>
public static class LinuxEncryption
{
    // Currently using placeholders as in the original Linux fork.
    // In a production environment, this could be swapped for a real cross-platform 
    // encryption lib (like AES) without touching the core project files.

    public static string Encrypt(string plainText)
    {
        return plainText;
    }

    public static string Decrypt(string encryptedData)
    {
        return encryptedData;
    }
}
