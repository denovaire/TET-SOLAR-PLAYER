using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NAudio.Midi;

namespace TetSolar.GUI.Runtime
{
    public sealed class MidiRecorder
    {
        readonly Stopwatch _sw = new();
        MidiEventCollection? _ctrl;
        MidiEventCollection? _out;
        int _ticksPerQuarter = 480;
        double _tempoBpm = 60.0; // default
        int _lastTick = 0;

        public bool IsRecording { get; private set; }
        public string? WorkingDir { get; private set; }
        public string ProjectName { get; private set; } = "project";

        public void Start(string workingDir, string projectName, double tempoBpm)
        {
            if (string.IsNullOrWhiteSpace(workingDir)) throw new InvalidOperationException("Working dir missing.");
            Directory.CreateDirectory(workingDir);

            WorkingDir = workingDir;
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? "project" : projectName.Trim();
            _tempoBpm = Math.Clamp(tempoBpm, 10.0, 300.0);

            _ctrl = new MidiEventCollection(1, _ticksPerQuarter);
            _out = new MidiEventCollection(1, _ticksPerQuarter);

            // Build first (and only) control track
            _ctrl.AddTrack();
            int microPerQuarter = (int)Math.Round(60000000.0 / _tempoBpm);
            _ctrl[0].Add(new TempoEvent(microPerQuarter, 0));
            _ctrl[0].Add(new TextEvent($"TET SOLAR CTRL start @ {DateTime.Now}", MetaEventType.TextEvent, 0));

            // Also tempo meta in output file, track 0
            _out.AddTrack();
            _out[0].Add(new TempoEvent(microPerQuarter, 0));
            _out[0].Add(new TextEvent($"TET SOLAR OUT start @ {DateTime.Now}", MetaEventType.TextEvent, 0));

            _sw.Restart();
            _lastTick = 0;
            IsRecording = true;
        }

        public void StopAndWrite()
        {
            if (!IsRecording || _ctrl == null || _out == null || WorkingDir == null) return;

            int endTick = NowTicks();
            _ctrl[0].Add(new MetaEvent(MetaEventType.EndTrack, 0, endTick));
            _out[0].Add(new MetaEvent(MetaEventType.EndTrack, 0, endTick));

            string ctrlPath = NextAvailablePath(Path.Combine(WorkingDir, $"{ProjectName}_ctrl.mid"));
            string outPath = NextAvailablePath(Path.Combine(WorkingDir, $"{ProjectName}_out.mid"));

            MidiFile.Export(ctrlPath, _ctrl);
            MidiFile.Export(outPath, _out);
            _sw.Stop();
            IsRecording = false;
        }

        public void LogControl(string text)
        {
            if (!IsRecording || _ctrl == null) return;
            int t = NowTicks();
            _ctrl[0].Add(new TextEvent(text, MetaEventType.TextEvent, t));
        }

        // Mirror an outgoing short message to output collection
        public void MirrorOutShortMessage(int channel1, MidiEvent ev)
        {
            if (!IsRecording || _out == null) return;
            // ensure track for channel exists (1..16 -> make 16 tracks if needed)
            while (_out.Tracks < 17) _out.AddTrack(); // track 1..16 for channels 1..16
            int trackIndex = Math.Clamp(channel1, 1, 16);
            int t = NowTicks();

            // clone event at current tick (NAudio events are mutable; create a copy)
            MidiEvent clone = ev switch
            {
                NoteOnEvent e => new NoteOnEvent(t, e.Channel, e.NoteNumber, e.Velocity, e.NoteLength),
                NoteEvent e => new NoteEvent(t, e.Channel, e.CommandCode, e.NoteNumber, e.Velocity),
                PitchWheelChangeEvent e => new PitchWheelChangeEvent(t, e.Channel, e.Pitch),
                ControlChangeEvent e => new ControlChangeEvent(t, e.Channel, e.Controller, e.ControllerValue),
                PatchChangeEvent e => new PatchChangeEvent(t, e.Channel, e.Patch),
                _ => new MetaEvent(MetaEventType.TextEvent, 0, t) // fallback (shouldn’t happen)
            };

            _out[trackIndex].Add(clone);
        }

        int NowTicks()
        {
            // time since start in seconds
            double sec = _sw.Elapsed.TotalSeconds;
            // ticks = seconds * (ppq * bpm / 60)
            double ticks = sec * (_ticksPerQuarter * _tempoBpm / 60.0);
            int t = (int)Math.Round(ticks);
            if (t < _lastTick) t = _lastTick; // monotonic
            _lastTick = t;
            return t;
        }


        static string NextAvailablePath(string path)
        {
            if (!System.IO.File.Exists(path)) return path;

            string dir = System.IO.Path.GetDirectoryName(path)!;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);

            int i = 1;
            string candidate;
            do
            {
                candidate = System.IO.Path.Combine(dir, $"{name}_{i}{ext}");
                i++;
            } while (System.IO.File.Exists(candidate));

            return candidate;
        }

    }
}
