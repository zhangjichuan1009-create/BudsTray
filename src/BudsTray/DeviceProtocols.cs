namespace RedmiBudsTray;

public enum EarphoneProtocol { Xiaomi, OneMore }

// Verified means a valid protocol response, not membership in a model whitelist.
public sealed record EarphoneProfile(EarphoneProtocol Protocol, bool Verified, bool IsHeadphone,
    bool? Anc = null, bool? Transparency = null);

public static class DeviceProtocols
{
    public static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static EarphoneProfile? Identify(string name)
    {
        var n = Normalize(name);
        if ((n.StartsWith("redmi") || n.StartsWith("xiaomi") || n.StartsWith("小米") || n.StartsWith("红米")) &&
            (n.Contains("buds") || n.Contains("earphone") || n.Contains("耳机")))
            return new(EarphoneProtocol.Xiaomi, false, false);
        if (n.StartsWith("1more") || n.StartsWith("万魔") || n.StartsWith("sonoflow"))
            return new(EarphoneProtocol.OneMore, false, n.Contains("sonoflow"));
        return null;
    }

    public static EarphoneProfile FromXiaomiInfo(RedmiDeviceInfo info, EarphoneProfile? current)
    {
        return current is { Protocol: EarphoneProtocol.Xiaomi } ? current with { Verified = true } : new(EarphoneProtocol.Xiaomi, true, false);
    }

    public static IReadOnlyList<PairedBluetoothDevice> Candidates(IEnumerable<PairedBluetoothDevice> devices) =>
        devices.Where(d => Identify(d.Name) is not null).DistinctBy(d => d.Address).ToArray();

    public static PairedBluetoothDevice? Choose(IReadOnlyList<PairedBluetoothDevice> devices, ulong? preferred,
        Func<ulong, bool> isAudioReady) => devices.FirstOrDefault(d => d.Address == preferred)
        ?? devices.FirstOrDefault(d => isAudioReady(d.Address)) ?? devices.FirstOrDefault();
}
