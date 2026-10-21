using System.Text;

namespace CleanSuite.Compat;

/// <summary>
/// Decodes encrypted fields of the original plug-in database (applications.ini, Encrypted=2):
/// Blowfish-ECB (big-endian halves), key = "HariBalu" + index:D8 + section name (ANSI),
/// input is upper-case hex, last plain byte = padding length.
/// Read-only compatibility; the replacement stores plug-ins in an open format.
/// </summary>
public static class LegacyPluginDecoder
{
    public static string Decode(string hex, string section, int index)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(0);   // system ANSI code page (CP_ACP), as used by the original
        var key = ansi.GetBytes("HariBalu" + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + section);
        var data = Convert.FromHexString(hex);
        if (data.Length == 0 || data.Length % 8 != 0) throw new FormatException("Encrypted field length must be a multiple of 8 bytes.");

        var bf = new Blowfish(key);
        for (int i = 0; i < data.Length; i += 8) bf.DecryptBlock(data, i);

        int pad = data[^1];
        int len = pad is >= 1 and <= 8 ? data.Length - pad : data.Length;
        int nul = Array.IndexOf(data, (byte)0, 0, len);
        if (nul >= 0) len = nul;
        return ansi.GetString(data, 0, len);
    }

    /// <summary>True if the key name is an encrypted field: File#, Reg#, DETECTR[#], DETECTF[#].</summary>
    public static bool TryGetIndex(string keyName, out int index)
    {
        index = 0;
        foreach (var prefix in new[] { "DETECTR", "DETECTF", "File", "Reg" })
        {
            if (!keyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = keyName[prefix.Length..];
            if (rest.Length == 0) return true;
            return int.TryParse(rest, out index);
        }
        return false;
    }

    private sealed class Blowfish
    {
        private readonly uint[] _p = (uint[])BlowfishTables.P.Clone();
        private readonly uint[] _s = (uint[])BlowfishTables.S.Clone();

        public Blowfish(byte[] key)
        {
            int j = 0;
            for (int i = 0; i < 18; i++)
            {
                uint d = 0;
                for (int k = 0; k < 4; k++) { d = (d << 8) | key[j]; j = (j + 1) % key.Length; }
                _p[i] ^= d;
            }
            uint l = 0, r = 0;
            for (int i = 0; i < 18; i += 2) { Encrypt(ref l, ref r); _p[i] = l; _p[i + 1] = r; }
            for (int i = 0; i < 1024; i += 2) { Encrypt(ref l, ref r); _s[i] = l; _s[i + 1] = r; }
        }

        private uint F(uint x) =>
            ((_s[x >> 24] + _s[256 + ((x >> 16) & 0xFF)]) ^ _s[512 + ((x >> 8) & 0xFF)]) + _s[768 + (x & 0xFF)];

        private void Encrypt(ref uint l, ref uint r)
        {
            for (int i = 0; i < 16; i++) { l ^= _p[i]; r ^= F(l); (l, r) = (r, l); }
            (l, r) = (r, l);
            r ^= _p[16];
            l ^= _p[17];
        }

        public void DecryptBlock(byte[] b, int o)
        {
            uint l = (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
            uint r = (uint)(b[o + 4] << 24 | b[o + 5] << 16 | b[o + 6] << 8 | b[o + 7]);
            for (int i = 17; i > 1; i--) { l ^= _p[i]; r ^= F(l); (l, r) = (r, l); }
            (l, r) = (r, l);
            r ^= _p[1];
            l ^= _p[0];
            b[o] = (byte)(l >> 24); b[o + 1] = (byte)(l >> 16); b[o + 2] = (byte)(l >> 8); b[o + 3] = (byte)l;
            b[o + 4] = (byte)(r >> 24); b[o + 5] = (byte)(r >> 16); b[o + 6] = (byte)(r >> 8); b[o + 7] = (byte)r;
        }
    }
}
