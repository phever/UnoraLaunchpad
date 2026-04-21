using System;
using System.Text;
#if !Linux
using System.Security.Cryptography;
#endif

namespace UnoraLaunchpad
{
    public static class EncryptionHelper
    {
        public static string EncryptString(string plainText)
        {
#if Linux
            return Linux.LinuxEncryption.Encrypt(plainText);
#else
            if (string.IsNullOrEmpty(plainText))
                return null;

            byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes = ProtectedData.Protect(plainTextBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
#endif
        }

        public static string DecryptString(string encryptedData)
        {
#if Linux
            return Linux.LinuxEncryption.Decrypt(encryptedData);
#else
            if (string.IsNullOrEmpty(encryptedData))
                return null;

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedData);
                byte[] plainTextBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainTextBytes);
            }
            catch (Exception)
            {
                return null;
            }
#endif
        }
    }
}
