using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using System.Text.Json;
using NAudio.Midi;
using TetSolar.Core;

class Program
{
    // ===== 31-EDO constants =====
    const int PitchMin = 1;        // C0 index
    const int PitchMax = 248;      // H7 index
    const int CenterDefault = 94;  // C3 index
    const int MidiC0 = 24;         // map our C0 -> MIDI 24  (C3 -> 60)

    // ===== runtime =====
    static MidiOut? midi;
    static List<(int ch1, int note)> active = new();
    static int pbRange = 2;
    static int baseChannel1 = 1;
    static int deviceIndex = -1;
    static int velocity = 96;

    class AppConfig
    {
        public string? PortName { get; set; } = "loopMIDI Port";
        public int? PortIndex { get; set; } = null;      // used if PortName not found
        public int PitchBend { get; set; } = 2;          // semitones
        public int BaseChannel1 { get; set; } = 1;       // 1..16
        public int Velocity { get; set; } = 96;          // 1..127
    }

    static string ConfigPath()
    {
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
        var dir = Path.GetDirectoryName(exe)!;
        return Path.Combine(dir, "tet31.config.json");
    }

    static AppConfig LoadConfig()
    {
        var path = ConfigPath();
        if (!File.Exists(path))
        {
            var cfg = new AppConfig();
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            Console.WriteLine($"[config] Created default {Path.GetFileName(path)} next to the EXE.");
            return cfg;
        }
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(text) ?? new AppConfig();
    }

    static int ResolveDevice(int? idx, string? name)
    {
        // prefer name if provided
        if (!string.IsNullOrWhiteSpace(name))
        {
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
                if (string.Equals(MidiOut.DeviceInfo(i).ProductName, name, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        // else use index if valid
        if (idx.HasValue && idx.Value >= 0 && idx.Value < MidiOut.NumberOfDevices)
            return idx.Value;

        // fallback: first device if any
        return MidiOut.NumberOfDevices > 0 ? 0 : -1;
    }

    // --------- HOTKEYS (expanded) ----------
    static readonly char[] SlotKeys = "1234567890qwertzuiopasdfghjklyxcvbnm".ToCharArray();

    // chord slots
    static readonly Dictionary<char, string> SlotCode = new();     // key -> shortcode
    static readonly Dictionary<char, List<int>> SlotChord = new();     // key -> base pcs (no transpose)
    static readonly Dictionary<char, string> SlotName = new();     // key -> display name (optional)
    static readonly Dictionary<char, int> SlotTranspose = new(); // key -> transpose offset (pc steps)
    static char currentKey = '\0';

    // ===== note-name translation (external file) =====
    static string[] NoteNames12 = new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "H" };

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // --- Load note-name translation ---
        NoteNames12 = LoadNoteNames();

        // --- Load config (MIDI + PB + velocity etc.) ---
        var cfg = LoadConfig();
        pbRange = Math.Clamp(cfg.PitchBend, 0, 24);
        baseChannel1 = Math.Clamp(cfg.BaseChannel1, 1, 16);
        velocity = Math.Clamp(cfg.Velocity, 1, 127);
        deviceIndex = ResolveDevice(cfg.PortIndex, cfg.PortName);

        if (deviceIndex < 0)
        {
            Console.WriteLine("ERROR: No MIDI OUT device available/resolved from config.");
            Console.WriteLine("Tip: edit tet31.config.json (PortName or PortIndex).");
            return;
        }

        Console.WriteLine($"[config] Port={(cfg.PortName ?? $"#{cfg.PortIndex}")}, PB=±{pbRange}, BaseCh={baseChannel1}, Vel={velocity}");
        Console.WriteLine($"Using device: [{deviceIndex}] {MidiOut.DeviceInfo(deviceIndex).ProductName}");

        // ===== collect chord lines (optional names) =====
        Console.WriteLine("\nPaste your chord lines (one per line). Allowed forms:");
        Console.WriteLine("  sym7spread37");
        Console.WriteLine("  merkur I<TAB>sym7spread37");
        Console.WriteLine("  merkur I  sym7spread37   (two+ spaces as separator)");
        Console.WriteLine($"Available slots ({SlotKeys.Length}): {new string(SlotKeys)}");
        Console.WriteLine("Finish with an empty line.\n");

        var lines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            lines.Add(line.Trim());
        }

        // assign to keys
        int count = Math.Min(lines.Count, SlotKeys.Length);
        for (int i = 0; i < count; i++)
        {
            var key = SlotKeys[i];
            var (name, tail) = ParseNameAndCode(lines[i]);
            try
            {
                List<int> pcs;
                string codeLabel;

                if (!TetSolarCore.LooksLikeShortcode(tail) && TetSolarCore.TryParsePcList(tail, out var direct))
                {
                    // direct PC list
                    pcs = direct;                         // already clamped/deduped/trimmed
                    codeLabel = "pc-list";
                }
                else
                {
                    // shortcode path (rand/sym/pairs/legacy)
                    pcs = TetSolarCore.ChordFromShortcode(tail);
                    codeLabel = tail;
                }

                SlotCode[key] = codeLabel;
                SlotChord[key] = pcs;
                if (!SlotTranspose.ContainsKey(key)) SlotTranspose[key] = 0;
                if (!string.IsNullOrEmpty(name)) SlotName[key] = name;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Skipping '{lines[i]}' (parse error): {ex.Message}");
            }
        }


        // open MIDI and prep channels
        using (midi = new MidiOut(deviceIndex))
        {
            Console.WriteLine($"\nOpened: {MidiOut.DeviceInfo(deviceIndex).ProductName}");
            for (int ch1 = 1; ch1 <= 16; ch1++) SendRpnPitchBendRange(midi, ch1, pbRange);

            Console.WriteLine("\nControls:");
            Console.WriteLine("  [1..0 q w e r t z u i o p a s d f g h j k l y x c v b n m] : select & play chord");
            Console.WriteLine("  Shift + (hotkey) : re-generate if chord is a rand…");
            Console.WriteLine("  + / - (main row or numpad) : transpose current chord by ±1 pc step");
            Console.WriteLine("  SPACE : stop / start    |    ESC : quit\n");

            // auto-play first slot
            foreach (var k in SlotKeys)
            {
                if (SlotChord.ContainsKey(k))
                {
                    // reset transpose when selecting
                    SlotTranspose[k] = 0;
                    TriggerChord(k);
                    break;
                }
            }

            // event loop
            bool playing = true;
            bool quit = false;
            while (!quit)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(true);
                    bool isShift = (keyInfo.Modifiers & ConsoleModifiers.Shift) != 0;

                    if (keyInfo.Key == ConsoleKey.Spacebar)
                    {
                        if (playing) { StopChord(); playing = false; }
                        else { if (currentKey != '\0') StartChord(GetTransposedPcs(currentKey)); playing = true; }
                    }
                    else if (keyInfo.Key == ConsoleKey.Escape)    // ESC ONLY to quit
                    {
                        AllNotesOffAndPanic();
                        quit = true;
                    }
                    else if (keyInfo.Key == ConsoleKey.Add || keyInfo.Key == ConsoleKey.OemPlus)
                    {
                        if (currentKey != '\0')
                        {
                            SlotTranspose[currentKey] = SlotTranspose.GetValueOrDefault(currentKey) + 1;
                            Console.WriteLine($"(transpose +1 → {SlotTranspose[currentKey]})");
                            ReprintAndRestart();
                            playing = true;
                        }
                    }
                    else if (keyInfo.Key == ConsoleKey.Subtract || keyInfo.Key == ConsoleKey.OemMinus)
                    {
                        if (currentKey != '\0')
                        {
                            SlotTranspose[currentKey] = SlotTranspose.GetValueOrDefault(currentKey) - 1;
                            Console.WriteLine($"(transpose -1 → {SlotTranspose[currentKey]})");
                            ReprintAndRestart();
                            playing = true;
                        }
                    }
                    else
                    {
                        char ch = char.ToLowerInvariant(keyInfo.KeyChar);
                        if (SlotChord.ContainsKey(ch))
                        {
                            if (isShift)
                            {
                                // SHIFT + hotkey : re-generate if rand…
                                if (SlotCode[ch].StartsWith("rand", StringComparison.OrdinalIgnoreCase))
                                {
                                    var pcsNew = TetSolarCore.ChordFromShortcode(SlotCode[ch]); // fresh
                                    SlotChord[ch] = pcsNew; // store base set
                                    Console.WriteLine("\nChord re-generated");
                                    // keep current transpose for that slot
                                    currentKey = ch;
                                    ReprintAndRestart();
                                    playing = true;
                                }
                                else
                                {
                                    Console.WriteLine("\nChord has no random elements.");
                                }
                            }
                            else
                            {
                                // normal select: reset transpose for that slot
                                SlotTranspose[ch] = 0;
                                TriggerChord(ch);
                                playing = true;
                            }
                        }
                    }
                }
                Thread.Sleep(5);
            }

            AllNotesOffAndPanic();
        }

        Console.WriteLine("Bye.");
    }

    // ====== name/code parsing & bold ======
    static (string? name, string code) ParseNameAndCode(string line)
    {
        // TAB as separator
        if (line.Contains('\t'))
        {
            var parts = line.Split('\t', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2) return (parts[0], parts[1]);
        }

        // two-or-more spaces as separator
        var m2 = Regex.Match(line, @"^(?<name>.+?)\s{2,}(?<code>\S.+)$");
        if (m2.Success) return (m2.Groups["name"].Value.Trim(), m2.Groups["code"].Value.Trim());

        // name + single-space + code (detect code by keyword start)
        int idx = IndexOfCodeStart(line);
        if (idx > 0) return (line[..idx].Trim(), line[idx..].Trim());

        // otherwise: only shortcode
        return (null, line.Trim());
    }

    static int IndexOfCodeStart(string s)
    {
        int best = -1;
        foreach (var tag in new[] { " sym", " pairs", " rand", "sym", "pairs", "rand" })
        {
            int i = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                int pos = i + (tag.StartsWith(' ') ? 1 : 0);
                best = (best < 0) ? pos : Math.Min(best, pos);
            }
        }
        // legacy like " ... 6int20"
        var m = Regex.Match(s, @"\s(\d+int\d+)\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            best = (best < 0) ? m.Groups[1].Index : Math.Min(best, m.Groups[1].Index);
        }
        return best;
    }

    static string Bold(string s) => $"\x1b[1m{s}\x1b[0m"; // ANSI bold

    static void TriggerChord(char ch)
    {
        StopChord();
        currentKey = ch;

        Console.WriteLine();
        var title = SlotName.TryGetValue(ch, out var nm) ? Bold(nm) : Bold(SlotCode[ch]);
        Console.WriteLine($"[{ch}]  {title}");

        var pcs = SlotChord[ch];
        Console.WriteLine($"{SlotCode[ch]}  ->  {string.Join(", ", pcs)}");

        var pcsPlayed = GetTransposedPcs(ch);
        var names = pcsPlayed.Select(pc => TetSolarCore.FormatNoteNameWithCents(pc, NoteNames12)).ToArray();
        Console.WriteLine(string.Join(", ", names));

        StartChord(pcsPlayed);
    }

    static void ReprintAndRestart()
    {
        if (currentKey == '\0') return;

        StopChord();

        var title = SlotName.TryGetValue(currentKey, out var nm) ? Bold(nm) : Bold(SlotCode[currentKey]);
        Console.WriteLine($"[{currentKey}]  {title}");
        Console.WriteLine($"{SlotCode[currentKey]}  ->  {string.Join(", ", SlotChord[currentKey])}");

        var pcsPlayed = GetTransposedPcs(currentKey);
        var names = pcsPlayed.Select(pc => TetSolarCore.FormatNoteNameWithCents(pc, NoteNames12)).ToArray();
        Console.WriteLine(string.Join(", ", names));

        StartChord(pcsPlayed);
    }

    // ===== play/stop =====
    static void StartChord(List<int> pcs)
    {
        active.Clear();

        // limit to available channels
        int voices = pcs.Count;
        int chNeeded = Math.Min(voices, Math.Max(1, 16 - (baseChannel1 - 1)));

        for (int i = 0; i < chNeeded; i++)
        {
            int ch1 = baseChannel1 + i;
            var (note, bend) = TetSolarCore.PcToMidiAndPB(pcs[i], pbRange);
            midi.Send(new PitchWheelChangeEvent(0, ch1, bend).GetAsShortMessage());
            midi.Send(new NoteOnEvent(0, ch1, note, velocity, 0).GetAsShortMessage());
            active.Add((ch1, note));
        }
    }

    static void StopChord()
    {
        foreach (var (ch1, note) in active)
            midi.Send(new NoteEvent(0, ch1, MidiCommandCode.NoteOff, note, 0).GetAsShortMessage());
        if (active.Count > 0) Console.WriteLine("[STOP]");
        active.Clear();
    }

    // ===== helpers: CC/RPN (1-based channels) =====
    static void SendCC(MidiOut midi, int ch1, int controller, int value)
        => midi.Send(new ControlChangeEvent(0, ch1, (MidiController)controller, value).GetAsShortMessage());

    static void SendRpnPitchBendRange(MidiOut midi, int ch1, int semis)
    {
        SendCC(midi, ch1, 101, 0);                    // RPN MSB
        SendCC(midi, ch1, 100, 0);                    // RPN LSB
        SendCC(midi, ch1, 6, Math.Clamp(semis, 0, 24));
        SendCC(midi, ch1, 38, 0);
        SendCC(midi, ch1, 101, 127);                  // RPN NULL
        SendCC(midi, ch1, 100, 127);
    }

    // ** hard panic to close all gates / kill hung notes **
    static void AllNotesOffAndPanic()
    {
        foreach (var (ch1, note) in active)
            midi.Send(new NoteEvent(0, ch1, MidiCommandCode.NoteOff, note, 0).GetAsShortMessage());
        active.Clear();

        for (int ch1 = 1; ch1 <= 16; ch1++)
        {
            midi.Send(new PitchWheelChangeEvent(0, ch1, 8192).GetAsShortMessage()); // center PB
            SendCC(midi, ch1, 123, 0); // All Notes Off
            SendCC(midi, ch1, 120, 0); // All Sound Off
        }
    }

    

    static string[] LoadNoteNames()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            var dir = Path.GetDirectoryName(exe)!;
            var path = Path.Combine(dir, "note_names.json");
            if (!File.Exists(path))
            {
                var json = JsonSerializer.Serialize(NoteNames12, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                Console.WriteLine($"[names] Created default note_names.json next to the EXE.");
                return NoteNames12;
            }
            var text = File.ReadAllText(path);
            var arr = JsonSerializer.Deserialize<string[]>(text);
            if (arr != null && arr.Length == 12) return arr;
            Console.WriteLine("[names] note_names.json invalid length; using defaults.");
            return NoteNames12;
        }
        catch
        {
            Console.WriteLine("[names] Failed to load note_names.json; using defaults.");
            return NoteNames12;
        }
    }

    static List<int> GetTransposedPcs(char key)
    {
        var basePcs = SlotChord[key];
        int t = SlotTranspose.GetValueOrDefault(key, 0);
        if (t == 0) return basePcs;
        var shifted = basePcs.Select(v => v + t).ToList();
        return TetSolarCore.ClampAndDedup(shifted);
    }
}
