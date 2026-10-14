using Microsoft.Win32;
using System.Text.Json;

namespace RedmiBudsTray;

public sealed record PanelDevice(ulong Address, string Name, EarphoneProfile? Profile = null, bool ProtocolConfirmed = false, bool DetectionFailed = false,
    EarphoneProtocol? ManualProtocol = null, bool? AncOverride = null, bool? TransparencyOverride = null, int DetectionVersion = 0);

public static class PanelDeviceStore
{
    private const string Key = @"Software\RedmiBudsTray";
    private static readonly object Gate = new();
    private static bool Demo => Environment.GetCommandLineArgs().Contains("--demo");
    private static PanelDevice[] _demo = [new(0x100000000001, "Redmi Buds 8 Pro", DeviceProtocols.Identify("Redmi Buds 8 Pro")), new(0x100000000002, "1MORE SonoFlow S", DeviceProtocols.Identify("1MORE SonoFlow S"))];
    public static PanelDevice[] Decode(string? json)
    {
        try { return (JsonSerializer.Deserialize<PanelDevice[]>(json ?? "[]") ?? []).Where(d => d.Address != 0 && d.Address <= 0xFFFFFFFFFFFF && !string.IsNullOrWhiteSpace(d.Name)).DistinctBy(d => d.Address)
            .Select(d => d.DetectionVersion < 2 && d.Profile is not null ? d with { Profile = d.Profile with { Verified = d.ProtocolConfirmed, Anc = null, Transparency = null } } : d).ToArray(); }
        catch (JsonException) { return []; }
    }
    public static PanelDevice[] Read()
    {
        if (Demo) return _demo.ToArray();
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return Decode(key?.GetValue("PanelDevices") as string);
    }
    private static void Save(IEnumerable<PanelDevice> devices)
    {
        if (Demo) { _demo = devices.ToArray(); return; }
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("PanelDevices", JsonSerializer.Serialize(devices), RegistryValueKind.String);
    }
    public static void Initialize(IReadOnlyList<PairedBluetoothDevice> paired, ulong? legacySelected)
    {
        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue("PanelDevices") is string) return;
            // Only migrate a device the existing user actually selected, never all paired devices.
            var previous = paired.FirstOrDefault(d => d.Address == legacySelected);
            Save(previous is null ? [] : [new PanelDevice(previous.Address, previous.Name, DeviceProtocols.Identify(previous.Name))]);
        }
    }
    public static void Add(PairedBluetoothDevice device)
    {
        lock (Gate)
        {
            var current = Read();
            if (current.Any(d => d.Address == device.Address)) return;
            Save(current.Append(new(device.Address, device.Name, DeviceProtocols.Identify(device.Name))));
        }
    }
    public static void UpdateProfile(ulong address, EarphoneProfile profile)
    {
        lock (Gate) Save(Read().Select(d => d.Address == address ? d with { Profile = profile, ProtocolConfirmed = profile.Verified, DetectionFailed = false, DetectionVersion = 2 } : d));
    }
    public static void Configure(ulong address, EarphoneProtocol? protocol, bool? anc, bool? transparency)
    {
        lock (Gate) Save(Read().Select(d => d.Address == address ? d with { ManualProtocol = protocol, AncOverride = anc, TransparencyOverride = transparency, DetectionFailed = false } : d));
    }
    public static void MarkDetectionFailed(ulong address)
    {
        lock (Gate) Save(Read().Select(d => d.Address == address ? d with { DetectionFailed = true } : d));
    }
    public static void Remove(ulong address)
    {
        lock (Gate) Save(Read().Where(d => d.Address != address));
    }
    public static IReadOnlyList<PairedBluetoothDevice> Resolve(IEnumerable<PanelDevice> managed, IReadOnlyList<PairedBluetoothDevice> paired) =>
        managed.Select(d => paired.FirstOrDefault(p => p.Address == d.Address) ?? new(d.Name, d.Address)).ToArray();
    public static IReadOnlyList<PairedBluetoothDevice> Devices() => Resolve(Read(), BluetoothDeviceFinder.FindAll());
    public static EarphoneProfile? Profile(PairedBluetoothDevice device) => Read().FirstOrDefault(d => d.Address == device.Address)?.Profile ?? DeviceProtocols.Identify(device.Name);
    public static PairedBluetoothDevice? Choose(ulong? preferred = null)
    {
        var devices = Devices();
        if (preferred.HasValue) return devices.FirstOrDefault(d => d.Address == preferred);
        return DeviceProtocols.Choose(devices, null, BluetoothAudioConnection.IsAudioReady);
    }
}
