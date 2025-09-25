using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using TetSolar.Core;

namespace TetSolar.GUI.Runtime
{
    public sealed class GuiRuntime : IDisposable
    {
        public event Action<string, List<int>>? OnChordChanged; // (displayName, pcsPlayedNow)
        public event Action<int>? OnTransposeDelta;             // +1 or -1 per call
        public event Action? OnRegen;                           // when rand chord is re-generated

        public MidiRecorder? Recorder { get; set; }
        public event Action<string, string, string>? OnInfoLines; // title, line2, line3
        public bool IsPlaying { get; private set; }
        public int PitchBendRange { get; private set; } = 2;
        public int BaseChannel1 { get; private set; } = 1;
        public int Velocity { get; private set; } = 96;
        public string OutDeviceName => _deviceIndex >= 0 ? MidiOut.DeviceInfo(_deviceIndex).ProductName : "(none)";

        // same hotkey order you use
        public static readonly char[] SlotKeys = "1234567890qwertzuiopasdfghjklyxcvbnm".ToCharArray();

        // slots
        readonly Dictionary<char, string> _slotCode = new();    // shortcode or "pc-list"
        readonly Dictionary<char, List<int>> _slotChord = new(); // pcs (base)
        readonly Dictionary<char, string> _slotName = new();    // display name (optional)
        readonly Dictionary<char, int> _slotTranspose = new();  // transpose per slot
        char _currentKey = '\0';



        MidiOut? _midi;
        int _deviceIndex = -1;
        readonly List<(int ch1, int note)> _active = new();
        string[] _noteNames12 = new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "H" };

        public void Initialize(string? portName, int? portIndex, int pitchBend, int baseCh1, int velocity, string[]? noteNames12 = null)
        {
            PitchBendRange = Math.Clamp(pitchBend, 0, 24);
            BaseChannel1 = Math.Clamp(baseCh1, 1, 16);
            Velocity = Math.Clamp(velocity, 1, 127);
            if (noteNames12 is { Length: 12 }) _noteNames12 = noteNames12;

            _deviceIndex = ResolveDevice(portIndex, portName);
            if (_deviceIndex < 0) throw new InvalidOperationException("No MIDI OUT device resolved.");
            _midi = new MidiOut(_deviceIndex);
            for (int ch1 = 1; ch1 <= 16; ch1++) SendRpnPitchBendRange(_midi, ch1, PitchBendRange);
        }

        public void Dispose()
        {
            Panic();
            _midi?.Dispose();
            _midi = null;
        }

        static int ResolveDevice(int? idx, string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                for (int i = 0; i < MidiOut.NumberOfDevices; i++)
                    if (string.Equals(MidiOut.DeviceInfo(i).ProductName, name, StringComparison.OrdinalIgnoreCase))
                        return i;
            if (idx.HasValue && idx.Value >= 0 && idx.Value < MidiOut.NumberOfDevices)
                return idx.Value;
            return MidiOut.NumberOfDevices > 0 ? 0 : -1;
        }

        public string GetCurrentShortcode()
        {
            if (_currentKey == '\0') return "";
            return _slotCode.TryGetValue(_currentKey, out var c) ? c : "";
        }

        public string GetCurrentNameOrCode()
        {
            if (_currentKey == '\0') return "";
            if (_slotName.TryGetValue(_currentKey, out var nm) && !string.IsNullOrWhiteSpace(nm)) return nm;
            return _slotCode.TryGetValue(_currentKey, out var c) ? c : "";
        }

        public IReadOnlyList<int> GetCurrentPcs()
        {
            if (_currentKey == '\0') return Array.Empty<int>();
            // Return the currently sounding (transposed) pcs
            var basePcs = _slotChord[_currentKey];
            int t = _slotTranspose.GetValueOrDefault(_currentKey, 0);
            if (t == 0) return basePcs;
            return TetSolarCore.ClampAndDedup(basePcs.Select(v => v + t).ToList());
        }



        public void LoadSlotsFromRows(IEnumerable<(string? name, string codeOrPcs)> rows)
        {
            _slotCode.Clear();
            _slotChord.Clear();
            _slotName.Clear();
            _slotTranspose.Clear();

            int slot = 0;

            foreach (var row in rows)
            {
                if (slot >= SlotKeys.Length) break;

                // Guard: skip empty lines
                if (string.IsNullOrWhiteSpace(row.codeOrPcs))
                    continue;

                string tail = row.codeOrPcs.Trim();

                try
                {
                    List<int> pcs;
                    string codeLabel;

                    // Prefer direct PC lists (e.g., "10, 90" or "2 7") when present
                    if (!TetSolarCore.LooksLikeShortcode(tail) && TetSolarCore.TryParsePcList(tail, out var direct))
                    {
                        pcs = direct;           // already clamped/deduped/trimmed by core
                        codeLabel = "pc-list";
                    }
                    else
                    {
                        pcs = TetSolarCore.ChordFromShortcode(tail); // sym / pairs / rand...
                        codeLabel = tail;
                    }

                    // Guard: must have at least 1 voice after clamping/dedup
                    if (pcs == null || pcs.Count == 0)
                        continue;

                    var key = SlotKeys[slot++];

                    _slotCode[key] = codeLabel;
                    _slotChord[key] = pcs;
                    _slotTranspose[key] = 0;

                    if (!string.IsNullOrWhiteSpace(row.name))
                        _slotName[key] = row.name!;
                }
                catch
                {
                    // Ignore this row and continue assigning the next valid one
                    continue;
                }
            }
        }


        public IEnumerable<(char key, string? name, string code, IReadOnlyList<int> pcs)> GetSlots()
        {
            foreach (var k in SlotKeys)
            {
                if (_slotChord.ContainsKey(k))
                {
                    _slotName.TryGetValue(k, out var nm);
                    yield return (k, nm, _slotCode[k], _slotChord[k]);
                }
            }
        }

        public void TriggerSlot(char key)
        {
            if (!_slotChord.ContainsKey(key))  // ← guard
            {
                OnInfoLines?.Invoke("Info", $"No chord assigned to '{key}'.", "");
                return;
            }
            Stop();
            _currentKey = key;
            _slotTranspose[key] = 0;
            PrintAndStart();
        }

        public void TogglePlay()
        {
            if (_currentKey == '\0')
            {
                // pick first assigned slot
                foreach (var k in SlotKeys)
                {
                    if (_slotChord.ContainsKey(k))
                    {
                        _currentKey = k;
                        _slotTranspose[k] = 0;
                        PrintAndStart();
                        return;
                    }
                }
                OnInfoLines?.Invoke("Info", "No chords loaded.", "");
                return;
            }

            if (IsPlaying) Stop();
            else StartChord(GetTransposedPcs(_currentKey));
        }


        public void RegenCurrentIfRandom()
        {
            if (_currentKey == '\0') return;
            var code = _slotCode[_currentKey];
            if (!code.StartsWith("rand", StringComparison.OrdinalIgnoreCase))
            {
                OnInfoLines?.Invoke("Info", "(no random)", "");
                return;
            }

            // *** ensure previous notes are off before new chord starts ***
            Stop();

            var pcsNew = TetSolarCore.ChordFromShortcode(code);
            _slotChord[_currentKey] = pcsNew;
            PrintAndStart("Chord re-generated");
            OnRegen?.Invoke();
        }


        public void TransposeCurrent(int delta)
        {
            if (_currentKey == '\0') return;
            _slotTranspose[_currentKey] = _slotTranspose.GetValueOrDefault(_currentKey) + delta;
            PrintAndStart($"transpose {(delta >= 0 ? "+" : "")}{delta} → {_slotTranspose[_currentKey]}");
            OnTransposeDelta?.Invoke(delta >= 0 ? +1 : -1);
        }
        public void Stop()
        {
            // Send NoteOff for all active notes (and mirror)
            foreach (var (ch1, note) in _active)
            {
                var offEv = new NoteEvent(0, ch1, MidiCommandCode.NoteOff, note, 0);
                _midi!.Send(offEv.GetAsShortMessage());
                Recorder?.MirrorOutShortMessage(ch1, offEv);
            }

            _active.Clear();
            IsPlaying = false;
        }



        public void Panic()
        {
            Stop();
            if (_midi == null) return;

            for (int ch1 = 1; ch1 <= 16; ch1++)
            {
                var pwCenter = new PitchWheelChangeEvent(0, ch1, 8192);
                _midi.Send(pwCenter.GetAsShortMessage());
                Recorder?.MirrorOutShortMessage(ch1, pwCenter);

                SendAndMirrorCC(ch1, 123, 0);
                SendAndMirrorCC(ch1, 120, 0);
            }
        }


        void PrintAndStart(string? info = null)
        {
            var title = _slotName.TryGetValue(_currentKey, out var nm) ? nm : _slotCode[_currentKey];
            var pcsBase = _slotChord[_currentKey];
            var line2 = $"{_slotCode[_currentKey]}  ->  {string.Join(", ", pcsBase)}";

            var pcsPlay = GetTransposedPcs(_currentKey);
            var names = string.Join(", ", pcsPlay.Select(pc => TetSolarCore.FormatNoteNameWithCents(pc, _noteNames12)));
            if (!string.IsNullOrWhiteSpace(info))
                OnInfoLines?.Invoke(title, info + "\n" + line2, names);
            else
                OnInfoLines?.Invoke(title, line2, names);

            // *** make sure the previous chord is fully off ***
            Stop();

            StartChord(pcsPlay);


            _slotName.TryGetValue(_currentKey, out var nameOrNull);
            OnChordChanged?.Invoke(
                nameOrNull ?? _slotCode[_currentKey],
                pcsPlay
            );


        }


        List<int> GetTransposedPcs(char key)
        {
            var basePcs = _slotChord[key];
            int t = _slotTranspose.GetValueOrDefault(key, 0);
            if (t == 0) return basePcs;
            var shifted = basePcs.Select(v => v + t).ToList();
            return TetSolarCore.ClampAndDedup(shifted);
        }

        void StartChord(List<int> pcs)
        {
            // Safety: turn any currently sounding notes off (and mirror)
            foreach (var (ch1Old, noteOld) in _active)
            {
                var offOld = new NoteEvent(0, ch1Old, MidiCommandCode.NoteOff, noteOld, 0);
                _midi!.Send(offOld.GetAsShortMessage());
                Recorder?.MirrorOutShortMessage(ch1Old, offOld);
            }
            _active.Clear();

            if (pcs == null || pcs.Count == 0 || _midi == null)
            {
                IsPlaying = false;
                return;
            }

            // Sort voices low→high, allocate channels with your bins/spill logic
            var pcsSorted = pcs.OrderBy(v => v).ToList();
            var channels = AllocateChannels(pcsSorted);
            int voices = Math.Min(pcsSorted.Count, channels.Count);
            if (voices <= 0)
            {
                IsPlaying = false;
                return;
            }

            for (int i = 0; i < voices; i++)
            {
                int ch1 = channels[i]; // absolute 1..16
                var (note, bend) = TetSolarCore.PcToMidiAndPB(pcsSorted[i], PitchBendRange);

                // PB first (microtuning), then NoteOn — both mirrored
                var pw = new PitchWheelChangeEvent(0, ch1, bend);
                _midi.Send(pw.GetAsShortMessage());
                Recorder?.MirrorOutShortMessage(ch1, pw);

                var on = new NoteOnEvent(0, ch1, note, Velocity, 0);
                _midi.Send(on.GetAsShortMessage());
                Recorder?.MirrorOutShortMessage(ch1, on);

                // Track active notes so Stop() can send NoteOffs (and mirror them)
                _active.Add((ch1, note));
            }

            IsPlaying = true;
        }





        void SendRpnPitchBendRange(MidiOut midi, int ch1, int semis)
        {
            // RPN 0,0 (Pitch Bend Sensitivity)
            SendAndMirrorCC(ch1, 101, 0);                      // RPN MSB
            SendAndMirrorCC(ch1, 100, 0);                      // RPN LSB
            SendAndMirrorCC(ch1, 6, Math.Clamp(semis, 0, 24)); // Data MSB
            SendAndMirrorCC(ch1, 38, 0);                      // Data LSB

            // RPN NULL
            SendAndMirrorCC(ch1, 101, 127);
            SendAndMirrorCC(ch1, 100, 127);
        }

        void SendAndMirrorCC(int ch1, int controller, int value)
        {
            var ccEv = new ControlChangeEvent(0, ch1, (MidiController)controller, value);
            _midi!.Send(ccEv.GetAsShortMessage());
            Recorder?.MirrorOutShortMessage(ch1, ccEv);
        }


        public bool IsAssigned(char key) => _slotChord.ContainsKey(key);


        // --- Voice → MIDI channel allocation (low/mid/high bins with spill & no-crossing) ---

        static readonly Random _rng = new();

        private static int? ChooseAndTake(List<int> pool)
        {
            if (pool.Count == 0) return null;
            int idx = _rng.Next(pool.Count);
            int ch = pool[idx];
            pool.RemoveAt(idx);
            return ch;
        }

        private List<int> AllocateChannels(IReadOnlyList<int> pcs)
        {
            // Pools
            var low = new List<int> { 1, 2, 3, 4, 5, 6 };
            var mid = new List<int> { 7, 8, 9, 10, 11, 12 };
            var high = new List<int> { 13, 14, 15, 16 };

            // We choose one channel per requested voice, using bins with spillover.
            // Then we sort the chosen channels and pair them with voices sorted by pc,
            // which guarantees no voice crossings while keeping the playful randomness.
            var chosen = new List<int>(Math.Min(pcs.Count, 16));

            foreach (var pc in pcs)
            {
                // Decide primary bin and spill order
                List<List<int>> order;
                if (pc <= 80) order = new() { low, mid, high };   // includes L0 (<=25)
                else if (pc >= 140) order = new() { high, mid, low };
                else order = new() { mid, low, high };

                int? picked = null;
                foreach (var pool in order)
                {
                    picked = ChooseAndTake(pool);
                    if (picked.HasValue) break;
                }
                if (picked.HasValue) chosen.Add(picked.Value);
                else break; // all pools empty (should only happen if asked > 16 voices)
            }

            // Sort ascending; StartChord will zip with pcs ascending to avoid crossings
            chosen.Sort();
            return chosen;
        }



    }
}
