namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// CA compiles names out; nodes, textures, clips and bones carry zlib CRC32 hashes of them. Weapon names are
    /// recovered by hashing a word list with the usual prefixes / suffixes (Mods/SOCOMCA/convert_weapons.cracker).
    /// </summary>
    public static class CaNames
    {
        private const string Words = @"m4a1 m4 m4a1_sd m16 m16a2 m16a4 ak47 ak74 aks74 aks74u ak101 ak103 m14 m21 m24 m40 m40a3 m82 m82a1 m107 sr25
sr25sd svd dragunov g3 g3a3 g3sg1 g36 g36c g36k hk416 hk417 hk21 m249 m60 m60e3 m60e4 m240 m240b pkm pkt rpk rpd mk43
mk46 mk48 mg3 mg42 m203 m79 mgl m32 at4 rpg rpg7 smaw m72 law stinger mp5 mp5sd mp5k mp7 ump ump45 p90 fnp90 mac10
mac11 mp9 tmp uzi bizon pp2000 m1014 m870 spas spas12 saiga saiga12 aa12 benelli mk23 mk23sd m9 m92 beretta deagle
desert_eagle glock glock17 glock18 sig226 sig_p226 hkp9s usp usp45 fiveseven five7 p9 makarov tt33 ots33 sig_commando
sig551 sig552 sig550 famas aug steyr_aug iw80 iw80a2 l85 l85a2 galil tar21 scar scar_h scar_l fal fnfal m4super90
m590 pancor jackhammer frag grenade m67 he_grenade flashbang smoke smokegrenade claymore satchel c4 m18 ammo knife
binoculars laser_designator designator radio scope silencer suppressor magazine bipod grip flashlight laser rifle
pistol shotgun smg sniper launcher rocket mortar mine";

        private static readonly string[] Prefixes = { "", "wep_", "w_", "weap_", "weapon_" };
        private static readonly string[] Suffixes = { "", "_3p", "_tp", "_world", "_hi", "_high", "_lo", "_low", "_sd", "_mag", "_clip" };

        private static Dictionary<uint, string>? _weapons;

        /// <summary>The weapon name with this CRC32, or null.</summary>
        public static string? Weapon(uint hash)
        {
            if (_weapons == null)
            {
                var table = new Dictionary<uint, string>();
                foreach (var w in Words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    foreach (var pre in Prefixes)
                        foreach (var suf in Suffixes)
                            foreach (var v in new[] { w, w.ToUpperInvariant(), char.ToUpperInvariant(w[0]) + w[1..] }.Distinct())
                                table.TryAdd(Crc32(pre + v + suf), pre + v + suf);
                _weapons = table;
            }
            return _weapons.TryGetValue(hash, out var name) ? name : null;
        }

        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        /// <summary>zlib CRC32 of the ASCII name (case-sensitive), how CA hashes every name.</summary>
        public static uint Crc32(string s)
        {
            uint c = 0xFFFFFFFFu;
            foreach (char ch in s) c = Table[(c ^ (byte)ch) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
