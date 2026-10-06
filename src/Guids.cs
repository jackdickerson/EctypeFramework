using System;
using System.Security.Cryptography;
using System.Text;

namespace Ectype
{
    /// <summary>
    /// Deterministic identity derivation: every value is a pure function of manifest strings,
    /// so all clients agree with no coordination. Renaming a classId or perk key derives a NEW
    /// GUID and orphans saved selections; manifests may pin an explicit "guid" to survive one.
    /// </summary>
    internal static class Guids
    {
        /// <summary>32-hex-char GUID string from a stable seed (MD5 - identity, not security).</summary>
        public static string Derive(string seed)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(seed));
                StringBuilder sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string PerkGuid(string classId, string perkKey)
        {
            return Derive("ectype:perk:" + classId + "/" + perkKey);
        }

        public static string TreeGuid(string classId)
        {
            return Derive("ectype:tree:" + classId);
        }

        /// <summary>
        /// Custom stat ids live under bit 29 (0x20000000), which no vanilla StatType uses; the
        /// low 28 bits come from the seed hash, so a collision between two custom stats is
        /// possible in principle but is detected and named at registration.
        /// </summary>
        public static int StatId(string classId, string statName)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(
                    Encoding.UTF8.GetBytes("ectype:stat:" + classId + "/" + statName));
                int low = BitConverter.ToInt32(hash, 0) & 0x0FFFFFFF;
                return 0x20000000 | low;
            }
        }
    }
}
