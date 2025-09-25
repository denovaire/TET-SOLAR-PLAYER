using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TetSolar.GUI.Runtime
{
    public sealed class CtrlJsonRecorder
    {
        // Cache options (analyzer: don't allocate per call)
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            WriteIndented = true
        };

        // ---------- JSON Models ----------
        public class EventBase
        {
            [JsonPropertyName("t")] public int T { get; set; }            // milliseconds since start
            [JsonPropertyName("type")] public string Type { get; set; } = ""; // "transport" | "slot" | "transpose" | "regen"
        }

        public sealed class ETransport : EventBase
        {
            [JsonPropertyName("op")] public string Op { get; set; } = "";    // e.g., "toggle"
        }

        public sealed class ESlot : EventBase
        {
            [JsonPropertyName("key")] public string Key { get; set; } = ""; // slot key (e.g., "q")
            [JsonPropertyName("name")] public string? Name { get; set; }     // display name
            [JsonPropertyName("code")] public string Code { get; set; } = ""; // shortcode or "pc-list"
            [JsonPropertyName("pcs")] public List<int> Pcs { get; set; } = new();
        }

        public sealed class ETranspose : EventBase
        {
            [JsonPropertyName("delta")] public int Delta { get; set; }       // +1 or -1
        }

        public sealed class ERegen : EventBase
        {
            [JsonPropertyName("key")] public string Key { get; set; } = "";
            [JsonPropertyName("code")] public string Code { get; set; } = "";
            [JsonPropertyName("pcs")] public List<int> Pcs { get; set; } = new();
        }

        public sealed class Doc
        {
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("project")] public string Project { get; set; } = "project";
            [JsonPropertyName("tempoBpm")] public double TempoBpm { get; set; } = 60.0;
            [JsonPropertyName("created")] public string Created { get; set; } = DateTime.UtcNow.ToString("o");
            [JsonPropertyName("events")] public List<EventBase> Events { get; set; } = new();
        }

        // ---------- Recorder ----------
        private readonly Stopwatch _sw = new();
        private Doc? _doc;

        public bool IsRecording => _doc != null;

        public void Start(string project, double bpm)
        {
            _doc = new Doc
            {
                Project = project,
                TempoBpm = bpm,
                Created = DateTime.UtcNow.ToString("o"),
                Events = new List<EventBase>()
            };
            _sw.Restart();
        }

        public void StopAndWrite(string workingDir)
        {
            if (_doc is null) return;
            _sw.Stop();

            string file = Path.Combine(workingDir, $"{_doc.Project}_ctrl.json");
            file = NextAvailablePath(file);

            string json = JsonSerializer.Serialize(_doc, s_jsonOptions);
            File.WriteAllText(file, json);

            _doc = null;
        }

        private int NowMs() => (int)Math.Max(0, _sw.ElapsedMilliseconds);

        public void LogTransport(string op)
        {
            if (_doc is null) return;
            _doc.Events.Add(new ETransport { T = NowMs(), Type = "transport", Op = op });
        }

        public void LogSlot(string key, string? name, string code, List<int> pcs)
        {
            if (_doc is null) return;
            _doc.Events.Add(new ESlot
            {
                T = NowMs(),
                Type = "slot",
                Key = key,
                Name = name,
                Code = code,
                Pcs = pcs
            });
        }

        public void LogTranspose(int delta)
        {
            if (_doc is null) return;
            _doc.Events.Add(new ETranspose { T = NowMs(), Type = "transpose", Delta = delta });
        }

        public void LogRegen(string key, string code, List<int> pcs)
        {
            if (_doc is null) return;
            _doc.Events.Add(new ERegen
            {
                T = NowMs(),
                Type = "regen",
                Key = key,
                Code = code,
                Pcs = pcs
            });
        }

        private static string NextAvailablePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int i = 1;
            string cand;
            do { cand = Path.Combine(dir, $"{name}_{i}{ext}"); i++; }
            while (File.Exists(cand));
            return cand;
        }
    }
}
