namespace RedmiBudsTray;

// Wire constants documented by Gadgetbridge's OneMorePacket implementation.
// Independently implemented bounded stream parser; SonoFlow S captures verify offsets.
public sealed record OneMoreReading(byte Command, int? Battery = null, string? Firmware = null,
    NoiseMode? Mode = null, bool? DualDevice = null, bool? Ldac = null);

public sealed class OneMoreProtocol
{
    private readonly List<byte> _buffer = new();
    public static byte[] InfoQuery => Convert.FromHexString("1101004E0000001C42");
    public static byte[] ModeQuery => Convert.FromHexString("1101005F0000000C43");
    public static byte[] DualQuery => Convert.FromHexString("110100770000000C6B");
    public static byte[] LdacQuery => Convert.FromHexString("1101006C0000000D71");
    public static byte[] SetMode(NoiseMode mode) => [.. Convert.FromHexString("1101005E000100135C"), mode switch { NoiseMode.Off => (byte)0, NoiseMode.Anc => (byte)1, NoiseMode.Transparency => (byte)3, _ => throw new ArgumentOutOfRangeException(nameof(mode)) }];
    public static byte[] SetDual(bool enabled) => [.. Convert.FromHexString("110100760001000D6A"), enabled ? (byte)1 : (byte)0];
    public static byte[] SetLdac(bool enabled) => [.. Convert.FromHexString("1101006B0001002D57"), enabled ? (byte)2 : (byte)0];

    public IEnumerable<OneMoreReading> Feed(byte[] bytes)
    {
        _buffer.AddRange(bytes);
        if (_buffer.Count > 4096) _buffer.RemoveRange(0, _buffer.Count - 4096);
        var result = new List<OneMoreReading>();
        while (_buffer.Count >= 4)
        {
            if (_buffer[0] != 1 || _buffer[1] != 1 || _buffer[2] != 0) { _buffer.RemoveAt(0); continue; }
            var command = _buffer[3];
            if (command is not (0x4E or 0x5F or 0x6C or 0x77)) { _buffer.RemoveAt(0); continue; }
            var length = command == 0x4E ? 19 : 10;
            if (_buffer.Count < length) break;
            var packet = _buffer.Take(length).ToArray();
            _buffer.RemoveRange(0, length);
            if (packet[4] != 0 || packet[6] != 0) continue;
            var value = packet[9];
            result.Add(command switch
            {
                0x4E => new(command, packet[13] <= 100 ? packet[13] : null, $"{packet[10]}.{packet[11]}.{packet[12]}"),
                0x5F => new(command, Mode: value switch { 0 => NoiseMode.Off, 1 => NoiseMode.Anc, 3 => NoiseMode.Transparency, _ => null }),
                0x6C => new(command, Ldac: value is 0 or 2 ? value == 2 : null),
                _ => new(command, DualDevice: value is 0 or 1 ? value == 1 : null)
            });
        }
        return result;
    }
}
