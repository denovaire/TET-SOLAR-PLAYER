using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;



namespace TetSolar.Core
{
    /// <summary>
    /// Pure engine for chord generation & pitch conversion in 31-EDO.
    /// Independent of MIDI or console I/O.
    /// </summary>
    /// 

    public enum ShortcodeToken
    {
        Random,     // rand / r
        Spread,     // spread / s
        Center,     // center / c
        Symmetry,   // sym
        Pairs       // pairs / p (falls gewünscht)
    }

    public static class ShortcodeLexer
    {
        // längere Alternativen zuerst, dann Kurzformen!
        // \b = Wortgrenzen; Args optional in (...) direkt mit einsammeln
        private static readonly Regex CmdRegex = new Regex(
            @"(?ix)                              # i: ignore case, x: verbose
              \b(?<cmd>rand|sym|pairs|spread|center|r|p|s|c)\b
              \s*(?<args>\([^)]*\))?             # optional: (…)
            ",
            RegexOptions.Compiled);

        public static IEnumerable<(ShortcodeToken token, string args, int index)> Tokenize(string src)
        {
            foreach (Match m in CmdRegex.Matches(src))
            {
                var cmd = m.Groups["cmd"].Value.ToLowerInvariant();
                var args = m.Groups["args"].Success ? m.Groups["args"].Value : string.Empty;

                yield return (Map(cmd), args, m.Index);
            }
        }

        private static ShortcodeToken Map(string cmd) => cmd switch
        {
            "rand" or "r" => ShortcodeToken.Random,
            "spread" or "s" => ShortcodeToken.Spread,
            "center" or "c" => ShortcodeToken.Center,
            "sym" => ShortcodeToken.Symmetry,
            "pairs" or "p" => ShortcodeToken.Pairs,
            _ => throw new ArgumentOutOfRangeException(nameof(cmd), $"Unknown shortcode: {cmd}")
        };
    }
    public static class TetSolarCore
    {
        // ===== 31-EDO constants =====
        public const int PitchMin = 1;        // C0 index
        public const int PitchMax = 248;      // H7 index
        public const int CenterDefault = 94;  // C3 index
        public const int MidiC0 = 24;         // map our C0 -> MIDI 24  (C3 -> 60)

        // ===== shortcode parsing =====
        public class Params
        {
            public string Kind = ""; // sym | pairs | rand | legacy-int
            public int Voices;
            public int Spread;       // <=0 means "full-range" ONLY for rand
            public int? Int;
            public int Center;
        }

        public static List<int> ChordFromShortcode(string sc)
        {
            if (string.IsNullOrWhiteSpace(sc))
                return new List<int>();

            sc = sc.Trim();

            // ===== PAIRS =====
            if (sc.StartsWith("pairs", StringComparison.OrdinalIgnoreCase))
            {
                // defaults
                int voices = 4;
                int spread = 31;     // your default spread (you asked for 31 if none provided)
                int intrvl = 8;      // default pair interval (adjust if needed)
                int center = 100;    // default center (adjust if needed)

                // 1) voices directly after "pairs"
                //    e.g. "pairs6..." -> voices=6
                var mVoices = System.Text.RegularExpressions.Regex.Match(
                    sc, @"^pairs(?<v>\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mVoices.Success && int.TryParse(mVoices.Groups["v"].Value, out var v))
                    voices = v;

                // 2) accept long+short aliases in ANY order:
                //    spreadNNN  or sNNN
                //    intNNN     or iNNN
                //    centerNNN  or cNNN
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(
                             sc,
                             @"(?:(?:spread|s)(-?\d+))|(?:(?:int|i)(-?\d+))|(?:(?:center|c)(-?\d+))",
                             System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    // exactly one of these groups will have a value in each match (if it matches at all)
                    if (m.Groups[1].Success) // spread/s
                    {
                        if (int.TryParse(m.Groups[1].Value, out var val)) spread = val;
                    }
                    else if (m.Groups[2].Success) // int/i
                    {
                        if (int.TryParse(m.Groups[2].Value, out var val)) intrvl = val;
                    }
                    else if (m.Groups[3].Success) // center/c
                    {
                        if (int.TryParse(m.Groups[3].Value, out var val)) center = val;
                    }
                }

                // (Optional) debug to Output window
                System.Diagnostics.Debug.WriteLine(
                    $"Parsed pairs: voices={voices}, spread={spread}, int={intrvl}, center={center}");

                return GenPairs(voices, spread, intrvl, center);
            }

            // … keep your other shortcode handlers (sym, rand, pairs of other kinds, etc.)
            // return whatever your existing code does for other patterns

            throw new ArgumentException($"Unrecognized shortcode: '{sc}'");
        }


        static Params ParseShortcode(string c)
        {
            var p = new Params();

            if (c.StartsWith("sym")) p.Kind = "sym";
            else if (c.StartsWith("pairs")) p.Kind = "pairs";
            else if (c.StartsWith("rand")) p.Kind = "rand";
            else throw new ArgumentException("Shortcode must start with 'sym', 'pairs', or 'rand' (or legacy like '6int20').");

            p.Voices = ReadSigned(c, p.Kind);

            // spread: support both "spread" and "s"
            int? sp = ReadOptionalSigned(c, "spread", "s");

            if (p.Kind == "rand")
            {
                // full-range if no spread given
                p.Spread = sp.HasValue ? sp.Value : 0;
            }
            else
            {
                // default to 31 for sym/pairs when omitted
                p.Spread = sp.HasValue ? sp.Value : 31;
            }

            if (p.Kind == "pairs")
                p.Int = ReadSigned(c, "int");

            // center: support "center" and "c"
            if (c.Contains("center") || c.Contains("c"))
                p.Center = ReadSigned(c, "center", ReadSigned(c, "c", CenterDefault));
            else
                p.Center = CenterDefault + ReadSigned(c, "offset", 0);

            return p;
        }



        static int? ReadOptionalSigned(string s, params string[] tags)
        {
            foreach (var tag in tags)
            {
                var m = Regex.Match(s, Regex.Escape(tag) + @"([+-]?\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            return null;
        }

        static int ReadSigned(string s, string tag, int def = int.MinValue)
        {
            var m = Regex.Match(s, Regex.Escape(tag) + @"([+-]?\d+)");
            if (!m.Success)
            {
                if (def != int.MinValue) return def;
                throw new ArgumentException($"Missing parameter '{tag}'.");
            }
            return int.Parse(m.Groups[1].Value);
        }



        // ===== generators =====
        private static (int a, int b) SplitInterval(int x) => (x / 2, x - x / 2);

        public static List<int> Clamp(List<int> xs)
            => xs.Select(x => Math.Max(PitchMin, Math.Min(PitchMax, x))).ToList();

        public static List<int> ClampAndDedup(List<int> pcs)
        {
            var seen = new HashSet<int>();
            var outList = new List<int>(pcs.Count);
            foreach (var v in pcs)
            {
                int x = Math.Max(PitchMin, Math.Min(PitchMax, v));
                if (seen.Add(x)) outList.Add(x);
            }
            return outList;
        }

        public static List<int> GenSym(int voices, int spread, int center)
        {
            var pcs = new List<int>();
            if (voices % 2 == 1)
            {
                int vs = (voices - 1) / 2;
                for (int k = -vs; k <= vs; k++) pcs.Add(center + k * spread);
            }
            else
            {
                var (a, b) = SplitInterval(spread);
                int m = voices / 2;
                for (int j = 0; j < m; j++)
                {
                    pcs.Add(center - (a + j * spread));
                    pcs.Add(center + (b + j * spread));
                }
                pcs.Sort();
            }
            return Clamp(pcs);
        }

        private static readonly Random Rng = new();

        public static List<int> GenRand(int voices, int spread, int center)
        {
            if (spread <= 0)
            {
                // full-range: pick unique PCs from [1..248]
                var set = new HashSet<int>();
                while (set.Count < voices)
                {
                    set.Add(Rng.Next(PitchMin, PitchMax + 1));
                }
                var list = set.ToList();
                list.Sort();
                return list;
            }

            // symmetric then jitter
            var baseSym = GenSym(voices, spread, center);
            double half = spread / 2.0;
            var pcs = baseSym.Select(v => (int)Math.Round(v + (Rng.NextDouble() * 2 - 1) * half)).ToList();
            pcs.Sort();
            return Clamp(pcs);
        }

        public static List<int> GenPairs(int voices, int spread, int intrvl, int center)
        {
            if (voices % 2 != 0) throw new ArgumentException("pairs requires an even number of voices.");
            var (a, b) = SplitInterval(intrvl);
            int m = voices / 2;
            var pcs = new List<int>(voices);
            for (int i = 0; i < m; i++)
            {
                int rel = i - (m - 1) / 2;
                int P = center + rel * spread;
                pcs.Add(P - a);
                pcs.Add(P + b);
            }
            pcs.Sort();

            return Clamp(pcs);
            
        }

        public static List<int> GenPairsAutoSpread(int voices, int intrvl, int center)
        {
            if (voices % 2 != 0) throw new ArgumentException("legacy intV requires even voices.");
            var (a, b) = SplitInterval(intrvl);
            int m = voices / 2;

            int best = 1;
            for (int d = 1; d <= 400; d++)
            {
                bool ok = true;
                for (int i = 0; i < m; i++)
                {
                    int rel = i - (m - 1) / 2;
                    int P = center + rel * d;
                    int vLo = P - a;
                    int vHi = P + b;
                    if (vLo < PitchMin || vHi > PitchMax) { ok = false; break; }
                }
                if (ok) best = d; else break;
            }

            var pcs = new List<int>(voices);
            for (int i = 0; i < m; i++)
            {
                int rel = i - (m - 1) / 2;
                int P = center + rel * best;
                pcs.Add(P - a);
                pcs.Add(P + b);
            }
            pcs.Sort();
            return pcs;
        }

        // ===== conversion to MIDI =====
        public static (int midiNote, int pb14) PcToMidiAndPB(int pcIndex, int pbRangeSemis)
        {
            int k = pcIndex - 1;
            int semis = (int)Math.Round(k * 12.0 / 31.0);
            double cents31 = k * (1200.0 / 31.0);
            double cents12 = semis * 100.0;
            double devCents = cents31 - cents12;

            int midi = MidiC0 + semis;

            const int PB_CENTER = 8192;
            const int PB_MAX = 16383;
            double scale = Math.Max(1.0, pbRangeSemis * 100.0);
            int bend = (int)Math.Round(PB_CENTER + (devCents / scale) * PB_CENTER);
            bend = Math.Max(0, Math.Min(PB_MAX, bend));

            return (midi, bend);
        }

        public static string FormatNoteNameWithCents(int pcIndex, string[] noteNames12)
        {
            int k = pcIndex - 1;
            int semis = (int)Math.Round(k * 12.0 / 31.0);
            double cents31 = k * (1200.0 / 31.0);
            double cents12 = semis * 100.0;
            double dev = cents31 - cents12;

            int octave = semis / 12;
            int pc12 = ((semis % 12) + 12) % 12;
            string name = noteNames12[pc12];

            int cents = (int)Math.Round(dev);
            string sign = cents > 0 ? "+" : (cents < 0 ? "" : "+");
            return $"{name}{octave}{sign}{cents}c";
        }

        public static bool TryParsePcList(string tail, out List<int> pcs)
        {
            pcs = new List<int>();
            var m = Regex.Matches(tail, @"\d+");
            if (m.Count == 0) return false;

            foreach (Match x in m)
            {
                if (int.TryParse(x.Value, out int v))
                    pcs.Add(v);
            }

            if (pcs.Count == 0) return false;
            pcs = ClampAndDedup(pcs).Take(16).ToList();


            return pcs.Count > 0;
        }

        public static bool LooksLikeShortcode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;

            s = s.Trim();

            // If it has any letters, assume it’s a shortcode:
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetter(s[i])) return true;
            }

            // Otherwise assume plain PC list if it matches numbers/space/comma/optional minus
            // e.g. "10, 90", "2 7", "-3, 15"
            return false;
        
        }

    }
}
