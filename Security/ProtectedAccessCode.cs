/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Security.Cryptography;
using System.Text;

namespace BryersHideoutGames.Security;

public static class ProtectedAccessCode
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("BryersHideoutGames/access-code/v1");

    public static string Encrypt(string plaintext)
    {
        var input = Encoding.UTF8.GetBytes(plaintext.Trim());
        try { return Convert.ToBase64String(ProtectedData.Protect(input, Purpose, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return string.Empty;
        var protectedBytes = Convert.FromBase64String(encrypted);
        try
        {
            var plaintext = ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
    }
}
