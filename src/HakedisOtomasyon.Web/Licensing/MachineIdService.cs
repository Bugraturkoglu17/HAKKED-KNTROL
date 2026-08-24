using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace HakedisOtomasyon.Web.Licensing;

/// <summary>
/// Her makineye özgü MachineId ve kısa cihaz kodu üretir.
/// UserName kasıtlı olarak dahil edilmez — aynı bilgisayarda
/// farklı Windows kullanıcılarının aynı lisansı kullanabilmesi için.
/// </summary>
public static class MachineIdService
{
    /// <summary>
    /// Windows MachineGuid + ComputerName birleştirilerek SHA-256 hash döner.
    /// Sonuç: 64 hex char (küçük harf).
    /// </summary>
    public static string GetMachineId()
    {
        var parts = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(guid))
                parts.Add(guid);
        }
        catch { /* registry erişim hatası yoksayılır */ }

        parts.Add(Environment.MachineName);

        var raw   = string.Join("|", parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Kullanıcıya gösterilen kısa cihaz kodu: SMK-XXXX-XXXX-XXXX
    /// </summary>
    public static string GetShortDeviceCode()
    {
        var hex = GetMachineId()[..12].ToUpperInvariant();
        return $"SMK-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }
}
