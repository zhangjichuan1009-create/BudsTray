using Microsoft.Win32;

namespace RedmiBudsTray;

internal static class ConnectionPreferences
{
    private const string Key = @"Software\RedmiBudsTray";
    public static ulong? SelectedAddress
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return ulong.TryParse(key?.GetValue("SelectedDevice") as string, System.Globalization.NumberStyles.HexNumber, null, out var address) ? address : null; }
        set { using var key = Registry.CurrentUser.CreateSubKey(Key); if (value.HasValue) key.SetValue("SelectedDevice", value.Value.ToString("X12")); else key.DeleteValue("SelectedDevice", false); }
    }
    public static bool IsPaused(ulong? address)
    {
        if (!address.HasValue) return Paused;
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        if (key?.GetValue($"Paused_{address:X12}") is int value) return value == 1;
        return address == SelectedAddress && Paused;
    }
    public static void SetPaused(ulong? address, bool paused)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (address.HasValue) key.SetValue($"Paused_{address:X12}", paused ? 1 : 0);
        Paused = paused;
    }
    public static bool Paused
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("ManuallyDisconnected") is int value && value == 1; }
        set { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue("ManuallyDisconnected", value ? 1 : 0, RegistryValueKind.DWord); }
    }
}
