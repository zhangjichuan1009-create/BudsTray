namespace RedmiBudsTray;

public sealed record MmaPacket(bool IsRequest, byte Opcode, byte Sequence, byte Status, byte[] Data);
public sealed record BatteryReading(int? Percent, bool Charging);
public sealed record EarbudBattery(BatteryReading Left, BatteryReading Right, BatteryReading Case);
public sealed record NoiseReading(NoiseMode Mode, byte Strength);
public sealed record WearingReading(bool Left, bool Right);
public enum RedmiSetting : ushort { AutoAnswer = 0x03, DualDevice = 0x04, EqPreset = 0x07, AdaptiveAnc = 0x25, AdaptiveSound = 0x29 }
public sealed record RedmiDeviceInfo(string? Firmware, int? VendorId, int? ProductId);
public sealed record RedmiSettingsReading(bool? DualDevice = null, bool? AdaptiveAnc = null, bool? AdaptiveSound = null,
    bool? AutoAnswer = null, byte? EqPreset = null);

// MMA implementation ported from Zhaoyi-ya/TWS-Pods-PC and maniacx/BudsLink (GPL-3.0).
public static class XiaomiProtocol
{
    private const ushort NoiseConfig = 0x000B;
    private static readonly byte[] Sbox = Convert.FromHexString(
        "64ac285ac9b337c50a10b7a3bab197463d05dc666ef69af80d589567c6aaabec"+
        "a0689b96d4ebbf434936e96a89d8c38a946399bc7bbec122bb5c71d51f92575d"+
        "8f44411d51e64017fbfd193234b8612aca236fda39f7a2017fd631e7de8004dd"+
        "2c5982afa8e00fcda1123e30d11cd03a33722e4f9002130675ce87c2efb2ad7d"+
        "3815e1529f7a6c2f27c4e281a9cf8dc0d7dfff6076148c5e5509e408c74220fc"+
        "d25091d94c629ee8b9a6f91a00210bfa359c4e4b6948cb0ec8a45bea8407b418"+
        "f4ae6bdba7cc3f8b4a0c3c25e5544d4583ed11f0b05393f27426b59d6d7cf32d"+
        "f156247e471b86bd708e1e3b731603b6ac285ac9b337c50a10b7a3bab1974688");
    private static readonly byte[] T1 = Convert.FromHexString(
        "012de293be4515ae780387a4b838cf3f08670994eb26a86bbd18341bbbbf72f7"+
        "4035489c512f3b55e3c09fd8d3f38db1ffa73edc8677d7a611fbf4ba92916483"+
        "f133efda2cb5b22b88d199cb8c841d14819771ca5fa38b573c82c4525c1ce8a0"+
        "04b4854af61354b6df0c1a8edee039fc209b244ea9989eabf260d06ceafac7d9"+
        "00d41f6e43bcec5389fe7a5d49c932c2f99af86d16db599644e9cde646428f0a"+
        "c1ccb965b0d2c6ac1e4162292e0e7450025ac3257b8a2a5bf0060d476f709d7e"+
        "10ce1227d54c4fd679306836757de4ed806a9037a25e76aac57f3dafa5e51961"+
        "fd4d7cb70beead4b22f5e7732321c805e166ddb3586963560fa1319517073a28");
    private static readonly byte[] T2 = Convert.FromHexString(
        "8000b00960efb9fd10129fe469baadf8c038c2654f0694fc19de6a1b5d4ea882"+
        "70ede8ec72b315c3ffabb6474401ac25c9fa8e411a21cbd30d6efe2658da320f"+
        "20a99d8498059cbb228c63e7c5e173c6af245b876627f757f496b1b75c8bd554"+
        "79dfaaf63ea3f111caf5d1177b9383bcbd521eebaeccd63508c88ab4e2cdbfd9"+
        "d050593f4d62340a4888b5564c2e6b9ed23d3c0313fb9751754a917123be762a"+
        "5ff9d4550bdc37311674d777a7e607dba42f46f3614567e30ca23b1c8518041d"+
        "29a08fb25ad8a67eee8d534ba19ac10e7a49a52c81c4c7362b7f439533f26c68"+
        "6df00228cedd9bea5e997c1486cfe542b840782d3ae9641f92907d396fe08930");
    private static readonly byte[] LinkKey = Convert.FromHexString("06775f87918dd423005df1d8cf0c142b");
    private static readonly byte[] BudsLinkSequence = Convert.FromHexString("11223333221111223333221111223333");
    private static readonly byte[][] BudsLinkCoefficients =
    [
        [2,1,1,1,4,2,1,1,2,2,4,2,4,4,16,8],
        [2,1,1,1,4,2,1,1,1,1,2,1,2,2,8,4],
        [1,1,4,2,2,2,4,2,16,8,4,4,2,1,1,1],
        [1,1,4,2,1,1,2,1,8,4,2,2,2,1,1,1],
        [16,8,2,2,4,2,4,4,1,1,4,2,1,1,2,1],
        [8,4,1,1,2,1,2,2,1,1,4,2,1,1,2,1],
        [2,2,4,2,4,4,16,8,2,1,1,1,4,2,1,1],
        [1,1,2,1,2,2,8,4,2,1,1,1,4,2,1,1],
        [4,2,4,4,16,8,2,2,1,1,2,1,1,1,4,2],
        [2,1,2,2,8,4,1,1,1,1,2,1,1,1,4,2],
        [4,4,16,8,1,1,2,1,4,2,1,1,4,2,2,2],
        [2,2,8,4,1,1,2,1,4,2,1,1,2,1,1,1],
        [1,1,2,1,1,1,4,2,4,4,16,8,2,2,4,2],
        [1,1,2,1,1,1,4,2,2,2,8,4,1,1,2,1],
        [4,2,1,1,2,1,1,1,4,2,2,2,16,8,4,4],
        [4,2,1,1,2,1,1,1,2,1,1,1,8,4,2,2]
    ];

    public static byte[] BuildAuthChallenge(byte[] challenge, byte sn) => BuildRequest(0x50, [0x01, .. challenge], sn);
    public static byte[] BuildAuthStatus(byte sn) => BuildRequest(0x51, [0x01, 0x00], sn);
    public static byte[] BuildBatteryQuery(byte sn) => BuildRequest(0x02, [0x00, 0x00, 0x00, 0x80], sn);
    public static byte[] BuildDeviceInfoQuery(byte sn) => BuildRequest(0x02, [0xFF, 0xFF, 0xFF, 0xFF], sn);
    public static byte[] BuildGetSetting(RedmiSetting setting, byte sn) => BuildRequest(0xF3, [(byte)((ushort)setting >> 8), (byte)setting], sn);
    public static byte[] BuildSetSetting(RedmiSetting setting, bool enabled, byte sn)
    {
        // Do not expose multipoint writes: changing it may interrupt Bluetooth audio.
        if (setting is not (RedmiSetting.AdaptiveAnc or RedmiSetting.AdaptiveSound or RedmiSetting.AutoAnswer))
            throw new ArgumentOutOfRangeException(nameof(setting));
        return BuildRequest(0xF2, [3, (byte)((ushort)setting >> 8), (byte)setting, enabled ? (byte)1 : (byte)0], sn);
    }
    public static byte[] BuildGetNoise(byte sn) => BuildRequest(0xF3, [0x00, 0x0B], sn);
    public static byte[] BuildWearingQuery(byte sn) => BuildRequest(0xF3, [0x00, 0x0C], sn);
    public static byte[] BuildRuntimeQuery(byte sn) => BuildRequest(0x09, [0xFF, 0xFF, 0xFF, 0xFF], sn);
    public static byte[] BuildSetNoise(NoiseMode mode, byte sn, byte strength = 0) => BuildRequest(0xF2, [0x04, 0x00, 0x0B, (byte)mode, strength], sn);
    public static byte[] BuildEarbudAuthResponse(byte[] challenge, byte sn) => BuildResponse(0x50, [0x01, .. ComputeBudsLinkAuth(challenge)], sn);
    public static byte[] BuildAuthConfirmResponse(byte sn) => BuildResponse(0x51, [0x01], sn);

    private static byte[] BuildRequest(byte opcode, byte[] data, byte sn)
    {
        var length = data.Length + 1;
        return [0xFE, 0xDC, 0xBA, 0xC4, opcode, (byte)(length >> 8), (byte)length, sn, .. data, 0xEF];
    }

    private static byte[] BuildResponse(byte opcode, byte[] data, byte sn)
    {
        var length = data.Length + 2;
        return [0xFE, 0xDC, 0xBA, 0x04, opcode, (byte)(length >> 8), (byte)length, 0x00, sn, .. data, 0xEF];
    }

    public static List<MmaPacket> Decode(byte[] input)
    {
        var result = new List<MmaPacket>();
        for (var offset = 0; offset + 8 <= input.Length;)
        {
            if (input[offset] != 0xFE || input[offset + 1] != 0xDC || input[offset + 2] != 0xBA) { offset++; continue; }
            var type = input[offset + 3]; var opcode = input[offset + 4];
            var length = (input[offset + 5] << 8) | input[offset + 6];
            var total = 8 + length;
            if (offset + total > input.Length) break;
            if (input[offset + total - 1] != 0xEF) { offset += total; continue; }
            var request = (type & 0x80) != 0;
            if (request && length >= 1)
                result.Add(new(true, opcode, input[offset + 7], 0, input[(offset + 8)..(offset + total - 1)]));
            else if (!request && length >= 2)
                result.Add(new(false, opcode, input[offset + 8], input[offset + 7], input[(offset + 9)..(offset + total - 1)]));
            offset += total;
        }
        return result;
    }

    public static bool VerifyAuth(byte[] challenge, MmaPacket packet)
    {
        if (packet.Status != 0 || packet.Data.Length != 17 || packet.Data[0] != 1) return false;
        var response = packet.Data[1..];
        return response.SequenceEqual(ComputeBudsLinkAuth(challenge)) || response.SequenceEqual(EncryptAuth(challenge));
    }

    public static EarbudBattery? TryReadBattery(MmaPacket packet)
    {
        if (packet.IsRequest || packet.Opcode != 0x02 || packet.Status != 0) return null;
        var fields = ReadFields(packet.Data, configuration: false);
        var data = fields?.FirstOrDefault(x => x.Id == 7).Value;
        return data?.Length == 3 ? new(ReadBattery(data[0]), ReadBattery(data[1]), ReadBattery(data[2])) : null;
    }

    private static List<(ushort Id, byte[] Value)>? ReadFields(byte[] data, bool configuration)
    {
        var result = new List<(ushort, byte[])>();
        var idSize = configuration ? 2 : 1;
        for (var offset = 0; offset < data.Length;)
        {
            var length = data[offset];
            if (length < idSize || offset + length + 1 > data.Length) return null;
            var id = configuration ? (ushort)((data[offset + 1] << 8) | data[offset + 2]) : data[offset + 1];
            result.Add((id, data[(offset + 1 + idSize)..(offset + 1 + length)]));
            offset += length + 1;
        }
        return result;
    }

    public static RedmiDeviceInfo? TryReadDeviceInfo(MmaPacket packet)
    {
        if (packet.IsRequest || packet.Status != 0 || packet.Opcode != 0x02) return null;
        var fields = ReadFields(packet.Data, configuration: false);
        if (fields is null) return null;
        string? firmware = null;
        int? vid = null, pid = null;
        foreach (var (id, value) in fields)
        {
            if (id == 1 && value.Length == 4)
            {
                string Version(byte a, byte b) => $"{a >> 4}.{a & 15}.{b >> 4}.{b & 15}";
                var primary = Version(value[0], value[1]);
                var secondary = Version(value[2], value[3]);
                if (primary != "0.0.0.0") firmware = secondary is "0.0.0.0" || primary == secondary ? primary : $"{primary} / {secondary}";
            }
            if (id == 3 && value.Length == 4) { vid = (value[0] << 8) | value[1]; pid = (value[2] << 8) | value[3]; }
        }
        return firmware is not null || vid.HasValue ? new(firmware, vid, pid) : null;
    }

    public static RedmiSettingsReading? TryReadSettings(MmaPacket packet)
    {
        if (packet.Status != 0 || packet.Opcode is not (0xF3 or 0xF4) || packet.IsRequest && packet.Opcode != 0xF4) return null;
        var fields = ReadFields(packet.Data, configuration: true);
        if (fields is null) return null;
        var reading = new RedmiSettingsReading();
        foreach (var (id, value) in fields)
        {
            if (value.Length != 1) continue;
            bool? flag = value[0] is 0 or 1 ? value[0] == 1 : null;
            reading = (RedmiSetting)id switch
            {
                RedmiSetting.DualDevice when flag.HasValue => reading with { DualDevice = flag },
                RedmiSetting.AdaptiveAnc when flag.HasValue => reading with { AdaptiveAnc = flag },
                RedmiSetting.AdaptiveSound when flag.HasValue => reading with { AdaptiveSound = flag },
                RedmiSetting.AutoAnswer when flag.HasValue => reading with { AutoAnswer = flag },
                RedmiSetting.EqPreset when value[0] is 0 or 1 or 5 or 6 or 10 => reading with { EqPreset = value[0] },
                _ => reading
            };
        }
        return reading == new RedmiSettingsReading() ? null : reading;
    }

    public static byte? TryReadAncStrength(MmaPacket packet)
    {
        if (packet.Status != 0 || packet.Opcode is not (0xF3 or 0xF4) || packet.IsRequest && packet.Opcode != 0xF4) return null;
        var fields = ReadFields(packet.Data, configuration: true);
        var value = fields?.FirstOrDefault(x => x.Id == NoiseConfig).Value;
        return value?.Length == 2 && value[0] == (byte)NoiseMode.Anc && value[1] <= 19 ? value[1] : null;
    }

    public static NoiseMode? TryReadNoise(MmaPacket packet)
        => TryReadNoiseDetails(packet)?.Mode;

    public static WearingReading? TryReadWearing(MmaPacket packet)
    {
        if (packet.Status != 0 || packet.Opcode is not (0xF3 or 0xF4)) return null;
        if (packet.Opcode == 0xF3 && packet.IsRequest) return null;
        var data = packet.Data;
        for (var offset = 0; offset < data.Length;)
        {
            var length = data[offset];
            if (length < 1 || offset + length + 1 > data.Length) return null;
            if (length == 3 && data[offset + 1] == 0 && data[offset + 2] == 0x0C)
            {
                var flags = data[offset + 3];
                if ((flags & 0xF0) != 0) return null;
                return new((flags & 0x08) != 0, (flags & 0x04) != 0);
            }
            offset += length + 1;
        }
        return null;
    }

    public static NoiseReading? TryReadNoiseDetails(MmaPacket packet)
    {
        if (packet.IsRequest && packet.Opcode != 0xF4 || packet.Opcode is not (0xF2 or 0xF3 or 0xF4) || packet.Status != 0) return null;
        var value = ReadFields(packet.Data, configuration: true)?.FirstOrDefault(field => field.Id == NoiseConfig).Value;
        return value?.Length is 1 or 2 && value[0] <= 2 ? new((NoiseMode)value[0], value.Length == 2 ? value[1] : (byte)0) : null;
    }

    private static BatteryReading ReadBattery(byte value)
    {
        var percent = value & 0x7F;
        return new(percent is >= 1 and <= 100 ? percent : null, (value & 0x80) != 0);
    }

    public static byte[] EncryptAuth(byte[] input)
    {
        if (input.Length != 16) throw new ArgumentException("Challenge must be 16 bytes");
        var state = LinkKey[..6].Concat(LinkKey[..6]).Concat(LinkKey[..4]).ToArray();
        return EncryptState(state, input);
    }

    public static byte[] ComputeBudsLinkAuth(byte[] input)
    {
        if (input.Length != 16) throw new ArgumentException("Challenge must be 16 bytes");
        var state = BudsLinkSequence.ToArray();
        var keyInput = input.ToArray(); keyInput[15] ^= 6;
        var keys = KeySchedule(keyInput); var backup = state.ToArray();
        for (var round = 0; round < 8; round++)
        {
            if (round == 2) state = KeyMix1(state, backup);
            state = KeyMix1(state, keys[round * 2]);
            for (var i = 0; i < 16; i++) state[i] = IsT1(i) ? T1[state[i]] : T2[state[i]];
            state = KeyMix2(state, keys[round * 2 + 1]);
            state = BudsLinkMix(state);
        }
        return KeyMix1(state, keys[16]);
    }

    private static byte[] EncryptState(byte[] state, byte[] input)
    {
        var keyInput = input.ToArray(); keyInput[15] ^= 6;
        var keys = KeySchedule(keyInput); var backup = state.ToArray();
        for (var round = 0; round < 8; round++)
        {
            if (round == 2) state = KeyMix1(state, backup);
            state = KeyMix1(state, keys[round * 2]);
            for (var i = 0; i < 16; i++) state[i] = IsT1(i) ? T1[state[i]] : T2[state[i]];
            state = KeyMix2(state, keys[round * 2 + 1]);
            state = Mix(state);
        }
        return KeyMix1(state, keys[16]);
    }

    private static byte[][] KeySchedule(byte[] input)
    {
        var keys = new byte[17][]; keys[0] = input.ToArray();
        var current = input.ToArray(); var buffer = new byte[17];
        for (var round = 0; round < 16; round++)
        {
            byte parity = 0;
            for (var i = 0; i < 16; i++) { parity ^= current[i]; buffer[i] = Rol3(current[i]); }
            buffer[16] = Rol3(parity); var next = new byte[16];
            for (var j = 0; j < 16; j++) next[j] = (byte)(Sbox[15 + 16 * round - j] + buffer[(round + 1 + j) % 17]);
            keys[round + 1] = next;
            for (var i = 0; i < 16; i++) current[i] = Rol3(current[i]);
        }
        return keys;
    }

    private static byte[] KeyMix1(byte[] x, byte[] key)
    {
        var output = x.ToArray();
        for (var i = 0; i < 16; i++) output[i] = Selected(i) ? (byte)(output[i] ^ key[i]) : (byte)(output[i] + key[i]);
        return output;
    }
    private static byte[] KeyMix2(byte[] x, byte[] key)
    {
        var output = x.ToArray();
        for (var i = 0; i < 16; i++) output[i] = Selected(i) ? (byte)(output[i] + key[i]) : (byte)(output[i] ^ key[i]);
        return output;
    }
    private static bool Selected(int index) => ((1 << index) & 0x9999) != 0;
    private static bool IsT1(int index) => index is 0 or 3 or 4 or 7 or 8 or 11 or 12 or 15;
    private static byte Rol3(byte value) => (byte)((value << 3) | (value >> 5));

    private static byte[] BudsLinkMix(byte[] state)
    {
        var output = new byte[16];
        for (var row = 0; row < 16; row++)
        {
            var sum = 0;
            for (var column = 0; column < 16; column++) sum += BudsLinkCoefficients[row][column] * state[column];
            output[row] = (byte)sum;
        }
        return output;
    }

    private static byte[] Mix(byte[] s)
    {
        int B(int x) => x & 255;
        int w16=s[0],w17=s[1],w3=s[2],w4=s[3],w5=s[4],w7=B(w17+2*w16),w6=s[5];
        w16=B(w17+w16); int w19=s[6],w20=B(w4+2*w3); w17=s[7]; w3=B(w4+w3);
        int w21=s[8],w22=B(w6+2*w5); w4=s[9]; w5=B(w6+w5); int w23=s[10],w24=B(w17+2*w19);
        w6=s[11]; w17=B(w17+w19); int w25=s[12],w26=B(w4+2*w21); w19=s[13]; w4=B(w4+w21);
        int w27=s[14],w28=B(w6+2*w23); w21=s[15]; w6=B(w6+w23); w23=B(w19+2*w25); w19=B(w19+w25);
        w25=B(w21+2*w27); w21=B(w21+w27); w27=B(w6+2*w26); w6=B(w6+w26); w26=B(w21+2*w23); w21=B(w21+w23);
        w23=B(w16+2*w20); w16=B(w20+w16); w20=B(w5+2*w24); w5=B(w24+w5); w24=B(w4+2*w28); w4=B(w28+w4);
        w28=B(w19+2*w25); w19=B(w25+w19); w25=B(w17+2*w7); w17=B(w17+w7); w7=B(w3+2*w22); w3=B(w22+w3);
        w22=B(w19+2*w24); w19=B(w19+w24); w24=B(w3+2*w25); w3=B(w25+w3); w25=B(w16+2*w20); w16=B(w20+w16);
        w20=B(w4+2*w28); w4=B(w28+w4); w28=B(w17+2*w7); w17=B(w17+w7); int w30=B(w20+w17); w17=B(w17+2*w20);
        w7=B(w5+2*w27); w5=B(w27+w5); w27=B(w21+2*w23); w21=B(w21+w23); int sp0=w17; w17=B(w19+w24);
        w23=B(w26+w6); w19=B(w19+2*w24); w20=B(w21+w7); w7=B(w21+2*w7); int sp5=w17; w17=B(w23+2*w25); int sp4=w19; w19=B(w4+w28);
        w4=B(w4+2*w28); int sp2=w7,sp6=w17; w17=B(w27+w5); w7=B(w23+w25); w5=B(w5+2*w27);
        int sp8=w4; w4=B(w22+w16); w16=B(w16+2*w22); int sp11=w17; w17=B(w6+2*w26); int sp1=w30,sp13=w4;
        w4=B(w17+w3); int sp12=w16; w16=B(w3+2*w17); int sp3=w20,sp7=w7,sp9=w19,sp10=w5,sp15=w4,sp14=w16;
        return [ (byte)sp0,(byte)sp1,(byte)sp2,(byte)sp3,(byte)sp4,(byte)sp5,(byte)sp6,(byte)sp7,(byte)sp8,(byte)sp9,(byte)sp10,(byte)sp11,(byte)sp12,(byte)sp13,(byte)sp14,(byte)sp15 ];
    }
}
