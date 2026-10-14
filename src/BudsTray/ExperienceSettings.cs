using Microsoft.Win32;

namespace RedmiBudsTray;

internal static class ExperienceSettings
{
    private const string Key = @"Software\RedmiBudsTray";
    public static bool LowBatteryEnabled { get => Read("LowBatteryEnabled") == 1; set => Write("LowBatteryEnabled", value ? 1 : 0); }
    public static int LowBatteryLatch { get => Read("LowBatteryLatch"); set => Write("LowBatteryLatch", value); }
    private static int Read(string name) { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(name) is int value ? value : 0; }
    private static void Write(string name, int value) { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue(name, value, RegistryValueKind.DWord); }
}

public sealed class LowBatteryPolicy
{
    public int Latch { get; private set; }
    public LowBatteryPolicy(int latch = 0) => Latch = latch & 7;
    public string[] Evaluate(BudsState state)
    {
        if (!state.Connected) return [];
        var warnings = new List<string>();
        Check(1, state.IsHeadphone ? "耳机" : "左耳", state.IsHeadphone ? state.HeadphoneBattery : state.LeftBattery, state.LeftCharging);
        Check(2, "右耳", state.RightBattery, state.RightCharging);
        Check(4, "充电盒", state.CaseBattery, state.CaseCharging);
        return warnings.ToArray();

        void Check(int bit, string name, int? value, bool charging)
        {
            if (value is null || value < 0 || value > 100) return;
            // Hysteresis prevents noisy 20/21% readings or reconnection from notifying again.
            if (charging || value >= 25) { Latch &= ~bit; return; }
            if (value <= 20 && (Latch & bit) == 0) { Latch |= bit; warnings.Add($"{name} {value}%"); }
        }
    }
}

public static class PopupPlacement
{
    public static (int X, int Y, int Width, int Height) Calculate(int left, int top, int right, int bottom, int anchorX, int anchorY, double scale, double panelHeight = 360)
    {
        var margin = Math.Max(0, Math.Min((int)Math.Round(12 * scale), (Math.Min(right - left, bottom - top) - 1) / 2));
        var width = Math.Min((int)Math.Round(420 * scale), Math.Max(1, right - left - 2 * margin));
        var height = Math.Min((int)Math.Round(panelHeight * scale), Math.Max(1, bottom - top - 2 * margin));
        var x = right - width - margin;
        var y = bottom - height - margin;
        return (x, Math.Max(top + margin, y), width, height);
    }
}
