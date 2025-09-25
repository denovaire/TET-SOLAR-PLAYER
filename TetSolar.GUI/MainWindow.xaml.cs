using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media; // for Brushes
using System.Text.Json;
using System.Text.Json.Serialization;
using TetSolar.GUI.Runtime;
using TetSolar.GUI.ViewModels;
using System.Collections.Generic;


namespace TetSolar.GUI
{

    // Disk schema for library files
    // TOP OF FILE (keep only this copy; delete any duplicates further down)
    // TOP OF FILE (keep only this copy; delete any duplicates further down)
    internal sealed class LibraryFile
    {
        public string? LibraryName { get; set; }
        public List<LibraryEntry> Entries { get; set; } = new();
    }

    internal sealed class LibraryEntry
    {
        public string? Name { get; set; }
        public string? Code { get; set; }
    }



    public partial class MainWindow : Window
    {
        
        
        readonly MainViewModel _vm = new();
        readonly GuiRuntime _rt = new();
        // pretty JSON for libraries
        private static readonly JsonSerializerOptions _jsonIndented = new() { WriteIndented = true };
        int _uiTransposeCounter = 0; // for status label only
        readonly MidiRecorder _rec = new();
        bool _isRecording = false;
        string _workingDir = "";
        string _projectName = "project";
        double _tempoBpm = 60.0;
        private readonly CtrlJsonRecorder _jrec = new();
        // ===== Live-mode state =====
        // private bool _liveOn = true;
        private System.Windows.Threading.DispatcherTimer? _liveTimer;

        private double _liveFontSize = 10;       // for LiveBox
        private readonly List<int> _liveCurrentPcs = new();
        private string _liveCurrentName = string.Empty;
        private bool _liveChordJustChanged = false;   // print name on next tick only
        private char? _liveMarker = null;             // '+', '-', 'R' one-shot marker
        private int _liveMaxLines = 200000;             // scrollback cap
                                                      // Black square for idle ticks
        private const char TickSquare = '\u2022';     // (black dot)
                                                      
        private string _lastShortcodeLine = string.Empty;
        // Prevent duplicate details on simple re-trigger
        private string? _lastPrintedChordSig = null;



        public MainWindow()
        {

            InitializeComponent();

            // 0) DataContext FIRST so bindings work
            DataContext = _vm;

            _vm.LibraryName =  _vm.LibraryName; // keep if JSON didn’t contain a name
                                                //  _vm.LibraryName = loadedName ?? _vm.LibraryName;

            LibraryGrid.PreparingCellForEdit += LibraryGrid_PreparingCellForEdit;
            // (optional) LibraryGrid.PreviewKeyDown += LibraryGrid_PreviewKeyDown_Global


            EnsureLibrariesDir();
            try
            {
                if (File.Exists(LastLibraryPath))
                {
                    var json = File.ReadAllText(LastLibraryPath);
                    var rows = System.Text.Json.JsonSerializer.Deserialize<List<LibraryRow>>(json);
                    if (rows != null && rows.Count > 0)
                    {
                        _vm.LibraryRows.Clear();
                        for (int i = 0; i < rows.Count && i < GuiRuntime.SlotKeys.Length; i++)
                        {
                            var r = rows[i];
                            r.SlotIndex = i;
                            r.TriggerLabel = TriggerLabelForIndex(i);
                            _vm.LibraryRows.Add(r);
                        }
                        _ = ApplyCipLogic_NoPlay(); // (re)map slots, do not start playback
                        _vm.StatusText = "Loaded last library.";
                    }
                }
            }
            catch (Exception ex)
            {
                _vm.StatusText = "Failed to load last library: " + ex.Message;
            }

            // 1) MIDI runtime + info sink
            try
            {
                _rt.Initialize(
                    portName: "loopMIDI Port",
                    portIndex: null,
                    pitchBend: 2,
                    baseCh1: 1,
                    velocity: 96,
                    noteNames12: null
                );
                _vm.MidiOutLabel = $"Out: {_rt.OutDeviceName}";
                _vm.PbLabel = $"PB: ±{_rt.PitchBendRange}";
                _vm.StatusText = "Ready. Keyboard shortcuts active.";
            }
            catch (Exception ex)
            {
                _vm.StatusText = "MIDI init failed: " + ex.Message;
            }

            


         

            // 4) Working dir
            _workingDir = LoadLastProjectPathOrDefault();
            WorkingDirBox.Text = _workingDir;
            // After loading _workingDir:
            EnsureWorkingLibrariesDir();
            TryLoadLibraryFromWorkingDir();


            // 5) Live mode: timer at 8 slices/beat, always ON
            _liveTimer = new System.Windows.Threading.DispatcherTimer();
            _liveTimer.Tick += OnLiveTick;
            SyncLiveTimerInterval();
            LiveBox.FontSize = _liveFontSize;
            _liveTimer.Start();

            // 6) Live overlays from engine
            _rt.OnChordChanged += (name, pcs) =>
            {
                _liveCurrentName = name ?? "";
                _liveCurrentPcs.Clear();
                _liveCurrentPcs.AddRange(pcs ?? Enumerable.Empty<int>());
                _liveChordJustChanged = true;
            };
            _rt.OnTransposeDelta += sign => { _liveMarker = sign >= 0 ? '+' : '-'; };
            _rt.OnRegen += () => { _liveMarker = 'R'; };
        }


        // ---- Project-folder + libraries root helpers ----
private static string FindProjectFolder()
{
    // Start at assembly location (bin\Debug\netX)
    var dir = new DirectoryInfo(AppContext.BaseDirectory);

    // Walk up a few levels looking for a *.csproj in the directory
    for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent!)
    {
        try
        {
            if (dir.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).Any())
                return dir.FullName;
        }
        catch { /* ignore and keep walking */ }
    }

    // Fallback: if not found, use one level up from bin folder if available
    var bin = new DirectoryInfo(AppContext.BaseDirectory);
    return bin.Parent?.Parent?.Parent?.FullName ?? AppContext.BaseDirectory;
}

private static string ProjectFolder => _projectFolder ??= FindProjectFolder();
private static string? _projectFolder;

private static string LibrariesRoot => Path.Combine(ProjectFolder, "libraries");
private static string LastLibraryPath => Path.Combine(LibrariesRoot, "last.json");
private static string LastProjectPathFile => Path.Combine(ProjectFolder, "last_project_path.txt");
private string _librariesDir = Path.Combine(AppContext.BaseDirectory, "libraries");
private string _lastProjectPathFile = Path.Combine(AppContext.BaseDirectory, "last_project_path.txt");



        // ===== Window focus helpers =====
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            FocusWindow();
        }

        private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Click anywhere to bring focus back so keys work immediately
            if (!IsKeyboardFocusWithin) FocusWindow();
        }

        private void FocusWindow()
        {
            Focusable = true;
            Focus();
            Keyboard.Focus(this);
        }

        // ===== Global keyboard handling =====
        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsTypingContext())
                return;
            try
            {
                
                // --- Transport & transpose first. Return immediately if handled. ---
                if (e.Key == Key.Space)
                {
                    // Always reload mapping from CIP before starting/continuing playback


                    _rt.TogglePlay(); // will start first chord if none selected
                    if (_jrec.IsRecording) _jrec.LogTransport("toggle");
                    _vm.StatusText = "Play/Stop";
                    e.Handled = true;
                    _rec.LogControl("Space TOGGLE");

                    return;
                }

                // --- Ctrl+N: add new row and start editing its Name ---
                if (e.Key == Key.N && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    AddNewRowAndEditName();
                    e.Handled = true;
                    return;
                }

                // Numpad * toggles recording
                if (e.Key == Key.Multiply)
                {
                    ToggleRecording();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Add || e.Key == Key.OemPlus)
                {
                    _rt.TransposeCurrent(+1);
                    _uiTransposeCounter += 1;
                    _vm.TransposeLabel = $"Transpose: +{_uiTransposeCounter}";
                    _vm.StatusText = "Transpose +1";
                    e.Handled = true;
                    _rec.LogControl("Transpose +1");
                    if (_jrec.IsRecording) _jrec.LogTranspose(+1);


                    FocusWindow();                    
                    return;
                }

                if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
                {
                    _rt.TransposeCurrent(-1);
                    _uiTransposeCounter -= 1;
                    _vm.TransposeLabel = $"Transpose: {(_uiTransposeCounter >= 0 ? "+" : "")}{_uiTransposeCounter}";
                    _vm.StatusText = "Transpose -1";
                    e.Handled = true;
                    _rec.LogControl("Transpose -1");
                    if (_jrec.IsRecording) _jrec.LogTranspose(-1); 

                    FocusWindow();                   
                    return;
                }

                // Ctrl+Enter → Apply CIP (re-parse), then move focus to Info
                if (e.Key == Key.Enter && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    OnApplyCip(null, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }


                if (e.Key == Key.Escape)
                {
                    _rt.Panic();
                    Application.Current.Shutdown();
                    e.Handled = true;
                    return;
                }

                // --- Slot selection / re-gen ---
                if (TryMapKeyToSlotKey(e.Key, out char slotKey))
                {
                    // Always reload mapping (no auto-play)


                    if (!_rt.IsAssigned(slotKey))
                    {
                        _vm.StatusText = $"No chord assigned to '{slotKey}'.";
                        e.Handled = true;
                        return;
                    }

                    // Robust Shift detection (don’t confuse with CapsLock)
                    bool shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                    if (shift)
                    {
                        // re-generate only if current slot is random
                        _rt.TriggerSlot(slotKey);                // select slot first (so regen applies to this)
                        _rt.RegenCurrentIfRandom();              // no-op if not random
                        if (_jrec.IsRecording)
                        {
                            var pcs = _rt.GetCurrentPcs();
                            var code = _rt.GetCurrentShortcode();
                            // the slotKey here is the same one you tested for
                            _jrec.LogRegen(slotKey.ToString(), code, pcs.ToList());
                        }

                        _vm.StatusText = "Re-generate (if random chord)";
                        e.Handled = true;
                        _rec.LogControl($"Regen slot '{slotKey}'");
                        FocusWindow();
                        return;
                    }
                    else
                    {
                        // normal select & play (no regen)
                        _uiTransposeCounter = 0;
                        _vm.TransposeLabel = "Transpose: +0";
                        _rt.TriggerSlot(slotKey);
                        _vm.StatusText = $"Slot '{slotKey}' triggered";
                        _rec.LogControl($"Trigger slot '{slotKey}'");
                        if (_jrec.IsRecording)
                        {
                            var pcs = _rt.GetCurrentPcs();
                            var code = _rt.GetCurrentShortcode();
                            var name = _rt.GetCurrentNameOrCode();
                            _jrec.LogSlot(slotKey.ToString(), name, code, pcs.ToList());
                        }

                        e.Handled = true;
                        FocusWindow();
                        return;
                    }
                }




            }
            catch (Exception ex)
            {
                _vm.StatusText = "PreviewKeyDown error: " + ex.Message;
                e.Handled = true; // swallow so it never crashes the app
            }
        }

        // Map WPF Key → your slot character sequence.
        // Supports digits row (D0..D9), NUMPAD (NumPad0..9), and letters,
        // in your exact order: "1234567890qwertzuiopasdfghjklyxcvbnm"
        private static bool TryMapKeyToSlotKey(Key key, out char slotKey)
        {
            slotKey = '\0';

            // digits row "1..0"
            if (key >= Key.D0 && key <= Key.D9)
            {
                string digits = "1234567890";
                if (key == Key.D0)
                {
                    slotKey = '0';
                    return true;
                }
                int idx = key - Key.D1; // D1..D9 => 0..8
                if (idx >= 0 && idx < 9)
                {
                    slotKey = digits[idx];
                    return true;
                }
            }

            // NUMPAD 0..9
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                int n = key - Key.NumPad0; // 0..9
                slotKey = (n == 0) ? '0' : (char)('0' + n); // '1'..'9' for 1..9
                return true;
            }

            // letters: map by literal Key names → char
            if (key >= Key.A && key <= Key.Z)
            {
                char c = (char)('a' + (key - Key.A));
                const string sequence = "qwertzuiopasdfghjklyxcvbnm";
                if (sequence.Contains(c)) // readability > IndexOf
                {
                    slotKey = c;
                    return true;
                }
            }

            return false;
            // (Note: we intentionally do NOT map '+' or '-' here; handled above.)
        }

        // ===== Live info sink from runtime =====
        private const int InfoMaxItems = 200;

        // ---- Working-dir scoped libraries ----
        private static string LibrariesDirFor(string workingDir)
            => Path.Combine(workingDir ?? "", "libraries");

        private void EnsureWorkingLibrariesDir()
        {
            if (string.IsNullOrWhiteSpace(_workingDir)) return;
            try { Directory.CreateDirectory(LibrariesDirFor(_workingDir)); } catch { }
        }

        private void AppendInfo(string title, string line2, string line3)
        {
            Dispatcher.Invoke(() =>
            {
                var line = (line2 ?? string.Empty).Trim();
                if (line.Length == 0) return;
                if (line.Equals(_lastShortcodeLine, StringComparison.Ordinal)) return;

                AppendLine(ShortcodeBox, line);
                AppendLine(ShortcodeBox, "");              // <- extra blank line

                _lastShortcodeLine = line;
            });
        }



        // ===== Libraries & last project =====





        

        private void EnsureLibrariesDir()
        {
            try { Directory.CreateDirectory(_librariesDir); } catch { /* ignore */ }
        }


        private string LoadLastProjectPathOrDefault()
        {
            try
            {
                if (File.Exists(LastProjectPathFile))
                {
                    var p = File.ReadAllText(LastProjectPathFile).Trim();
                    if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return p;
                }
            }
            catch { }

            // Fallback: the project folder itself
            return ProjectFolder;
        }

        private void SaveLastProjectPath(string path)
        {
            try { File.WriteAllText(LastProjectPathFile, path ?? ""); } catch { }
        }





        // ===== Buttons =====
        private void OnPlayStop(object sender, RoutedEventArgs e) => _rt.TogglePlay();


        private void OnLoadMidi(object sender, RoutedEventArgs e)
        {
            _vm.StatusText = "Load MIDI (stub) – playback will be added later.";
        }

        private void OnQuit(object sender, RoutedEventArgs e)
        {
            _rt.Panic();
            Application.Current.Shutdown();
        }

        private void OnBrowseMidi(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "MIDI file|*.mid", FileName = _vm.MidiFilePath };
            if (dlg.ShowDialog() == true)
            {
                _vm.MidiFilePath = dlg.FileName;
                // ProjectNameBox.Text = _vm.MidiFilePath;
            }
        }

        // Button: Apply CIP (Input Chords)
        // Loads the current table into the engine and NEVER starts playback.
        // Button: Apply (re-parse table into engine; never auto-play; move focus to Info)
        private void OnApplyCip(object? sender, RoutedEventArgs e)
        {
            try
            {
                int n = ApplyCipLogic_NoPlay();
                _vm.StatusText = $"{n} chords applied.";
                FocusWindow(); // keep shortcuts live after clicking the button
            }
            catch (Exception ex)
            {
                _vm.StatusText = "Apply failed: " + ex.Message;
            }
        }



        // current view mode for the left (read-only) column
        bool _showTypingKeys = true; // default: show "123" labels

        // typing-key sequence in your exact hotkey order
        const string TypingKeySequence = "1234567890qwertzuiopasdfghjklyxcvbnm";

        // Update all TriggerLabel values according to the current mode
        private void UpdateTriggerLabels()
        {
            for (int i = 0; i < _vm.LibraryRows.Count; i++)
            {
                if (_showTypingKeys)
                {
                    // show typing keys: 1..0 qwertzuiop asdfghjkl yxcvbnm
                    string shown = (i < TypingKeySequence.Length)
                        ? TypingKeySequence[i].ToString()
                        : "";
                    _vm.LibraryRows[i].TriggerLabel = shown;
                }
                else
                {
                    // show musical labels from C3 upward
                    _vm.LibraryRows[i].TriggerLabel = TriggerLabelForIndex(i);
                }
            }

            // update button caption to reflect current mode
            if (KeyLabelToggleBtn != null)
                KeyLabelToggleBtn.Content = _showTypingKeys ? "123" : "MIDI";
        }

        // Button handler: toggle the mode and refresh labels
        private void OnToggleKeyLabels(object sender, RoutedEventArgs e)
        {
            _showTypingKeys = !_showTypingKeys;
            UpdateTriggerLabels();
        }


        private void OnNewRow(object sender, RoutedEventArgs e)
        {
            int idx = _vm.LibraryRows.Count;
            _vm.LibraryRows.Add(new LibraryRow
            {
                SlotIndex = idx,
                TriggerLabel = TriggerLabelForIndex(idx),
                Name = "",
                Code = ""
            });

            UpdateTriggerLabels(); // ← refresh left column labels
        }

        private void OnDuplicateRow(object sender, RoutedEventArgs e)
        {
            if (LibraryGrid.SelectedItem is LibraryRow row)
            {
                int idx = _vm.LibraryRows.Count;
                _vm.LibraryRows.Add(new LibraryRow
                {
                    SlotIndex = idx,
                    Name = row.Name,
                    Code = row.Code
                });

                UpdateTriggerLabels(); // ← refresh after duplication
            }
        }


        private void OnDeleteRow(object sender, RoutedEventArgs e)
        {
            if (LibraryGrid.SelectedItem is LibraryRow row)
            {
                _vm.LibraryRows.Remove(row);

                // reindex slots
                for (int i = 0; i < _vm.LibraryRows.Count; i++)
                    _vm.LibraryRows[i].SlotIndex = i;

                UpdateTriggerLabels(); // ← refresh after deletion
            }
        }


        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _rt.Dispose();
        }

        // helper: label C3 upward
        private static string TriggerLabelForIndex(int i)
        {
            int note = 60 + i; // C3=60 upward just for label
            string[] nn = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "H" };
            int pc = ((note % 12) + 12) % 12;
            int oct = note / 12 - 1; // MIDI octave conv
            return $"{nn[pc]}{oct}";
        }
        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {

        }
        private bool IsTypingContext()
        {
            if (LibraryGrid.IsKeyboardFocusWithin) return true;
            // if (ProjectNameBox.IsKeyboardFocusWithin) return true;

            var fe = FocusManager.GetFocusedElement(this) as FrameworkElement;
            if (fe is TextBox) return true;

            return false;
        }

        // Apply CIP rows to the engine; never auto-play unless explicitly requested.
        // Apply CIP rows to the engine; never auto-play.
        // Skips empty Code rows; shows a clear status message with counts.
        private void ApplyCipToEngine(bool autoStart)
        {
            var rowsAll = _vm.LibraryRows
                             .OrderBy(r => r.SlotIndex)
                             .ToList();

            var usable = rowsAll
                         .Where(r => !string.IsNullOrWhiteSpace(r.Code))
                         .Select(r => (name: string.IsNullOrWhiteSpace(r.Name) ? null : r.Name,
                                       codeOrPcs: r.Code))
                         .ToList();

            if (usable.Count == 0)
            {
                _vm.StatusText = "No chords to apply (Code/PCs column is empty).";
                return;
            }

            try
            {
                _rt.LoadSlotsFromRows(usable);

                // reset transpose display after apply
                _uiTransposeCounter = 0;
                _vm.TransposeLabel = "Transpose: +0";

                if (autoStart)
                {
                    // Optional: start the first one — we don't use this path now
                    var first = usable.FirstOrDefault();
                    if (first.codeOrPcs != null)
                    {
                        // We don’t know which slot key it mapped to here; leave it silent
                    }
                }

                _vm.StatusText = $"Applied {usable.Count} chord(s). Press SPACE to start or hit a hotkey.";
            }
            catch (Exception ex)
            {
                _vm.StatusText = "Apply failed: " + ex.Message;
            }
        }

        private void AddNewRowAndEditName()
        {
            int idx = _vm.LibraryRows.Count;

            // Optional cap: do not exceed available slot keys
            if (idx >= GuiRuntime.SlotKeys.Length)
            {
                _vm.StatusText = "All slots assigned";
                return;
            }

            var row = new LibraryRow
            {
                SlotIndex = idx,
                Name = "",
                Code = ""
                // TriggerLabel will be set by UpdateTriggerLabels() to match toggle (123/MIDI)
            };
            _vm.LibraryRows.Add(row);

            // Refresh left-column labels according to toggle
            UpdateTriggerLabels();

            // Bring new row into view and begin editing Name (column index 1)
            LibraryGrid.Items.Refresh();
            LibraryGrid.SelectedItem = row;
            LibraryGrid.ScrollIntoView(row);
            LibraryGrid.CurrentCell = new DataGridCellInfo(row, LibraryGrid.Columns[1]); // 0=Key, 1=Name, 2=Code
            LibraryGrid.BeginEdit();

            _vm.StatusText = "New row added (Ctrl+N)";
        }

        private void OnBrowseWorkingDir(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose Working Directory for MIDI files",
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(_workingDir) ? _workingDir : ProjectFolder   // <--
            })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _workingDir = dlg.SelectedPath;
                    WorkingDirBox.Text = _workingDir;
                    SaveLastProjectPath(_workingDir);
                    _vm.StatusText = "Working Dir set.";
                }
            }
        }




        private void OnProjectNameChanged(object sender, TextChangedEventArgs e)
        {
            // _projectName = ProjectNameBox.Text?.Trim() ?? "project";
            return;
        }

        private void OnBpmPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Allow digits, dot, and one dot only
            e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(
                ((TextBox)sender).Text.Insert(((TextBox)sender).CaretIndex, e.Text),
                @"^\d*\.?\d*$");
        }

        private void OnBpmLostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(BpmBox.Text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                _tempoBpm = Math.Clamp(v, 10.0, 300.0);
                SyncLiveTimerInterval();

                BpmBox.Text = _tempoBpm.ToString("0.0");
            }
            else
            {
                _tempoBpm = 60.0;
                BpmBox.Text = "60.0";
            }
        }

        private void OnRecord(object sender, RoutedEventArgs e)
        {
            ToggleRecording();
        }

        private void ToggleRecording()
        {
            if (!_isRecording)
            {
                if (string.IsNullOrWhiteSpace(_workingDir))
                {
                    System.Windows.MessageBox.Show("Please specify a path for your files first", "TET SOLAR",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    _vm.StatusText = "Recording blocked: Working Dir missing.";
                    return;
                }

                // start
                _rec.Start(_workingDir, _projectName, _tempoBpm);
                _rt.Recorder = _rec;
                _jrec.Start(_projectName, _tempoBpm);
                _rt.Recorder = _rec;
                _vm.StatusText = "Recording… (JSON+MIDI)";
                _isRecording = true;

                RecordBtn.Content = "Stop";
                RecordBtn.Background = System.Windows.Media.Brushes.IndianRed;
                _vm.StatusText = $"Recording… ({_projectName}_ctrl.mid / {_projectName}_out.mid)";

                _rec.LogControl("Record START");
            }
            else
            {
                // stop & write
                _rec.LogControl("Record STOP");
                _rt.Recorder = null;   // <-- detach so normal play doesn’t mirror
                _rec.StopAndWrite();
                _jrec.StopAndWrite(_workingDir);
                _rt.Recorder = null;
                _isRecording = false;

                RecordBtn.Content = "Record";
                RecordBtn.Background = System.Windows.Media.Brushes.Gray;
                _vm.StatusText = "Recording finished. Files written.";
            }

            // keep keyboard focus on window for performance
            FocusWindow();
        }



        private void LibraryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {

            try
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    // commit current edit before applying
                    if (LibraryGrid.CommitEdit(DataGridEditingUnit.Cell, true))
                        LibraryGrid.CommitEdit(DataGridEditingUnit.Row, true);

                    OnApplyCip(null, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _vm.StatusText = "LibraryGrid key error: " + ex.Message;
            }



            if (e.Key != Key.Tab) return;
            if (LibraryGrid.CurrentCell.Column == null) return;

            // Column indices: 0=Key, 1=Name, 2=Code
            const int KEY_COL = 0, NAME_COL = 1, CODE_COL = 2;

            int colIndex = LibraryGrid.Columns.IndexOf(LibraryGrid.CurrentCell.Column);
            int rowIndex = LibraryGrid.Items.IndexOf(LibraryGrid.CurrentItem);

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            int maxSlots = GuiRuntime.SlotKeys.Length;

            // Never focus Key column; if somehow there, jump to Name same row
            if (colIndex == KEY_COL)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    LibraryGrid.CurrentCell = new DataGridCellInfo(LibraryGrid.Items[rowIndex], LibraryGrid.Columns[NAME_COL]);
                    LibraryGrid.BeginEdit();
                }));
                return;
            }

            if (!shift)
            {
                // From NAME -> CODE (same row). No row creation here.
                if (colIndex == NAME_COL)
                {
                    e.Handled = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        LibraryGrid.CurrentCell = new DataGridCellInfo(LibraryGrid.Items[rowIndex], LibraryGrid.Columns[CODE_COL]);
                        LibraryGrid.BeginEdit();
                    }));
                    return;
                }

                // From CODE -> NAME (next row). Create next row if needed (until max).
                if (colIndex == CODE_COL)
                {
                    e.Handled = true;
                    int targetRow = rowIndex + 1;

                    if (targetRow >= _vm.LibraryRows.Count)
                    {
                        if (_vm.LibraryRows.Count >= maxSlots)
                        {
                            _vm.StatusText = "All slots assigned";
                            return;
                        }

                        // Create new row
                        int idx = _vm.LibraryRows.Count;
                        _vm.LibraryRows.Add(new LibraryRow
                        {
                            SlotIndex = idx,
                            TriggerLabel = TriggerLabelForIndex(idx),
                            Name = "",
                            Code = ""
                        });
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        int r = Math.Min(rowIndex + 1, _vm.LibraryRows.Count - 1);
                        LibraryGrid.SelectedIndex = r;
                        LibraryGrid.CurrentCell = new DataGridCellInfo(LibraryGrid.Items[r], LibraryGrid.Columns[NAME_COL]);
                        LibraryGrid.BeginEdit();
                    }));
                    return;
                }
            }



            // SHIFT+TAB: let DataGrid do its default backward navigation
        }
        // Button: Apply CIP (re-parse table into engine; never auto-play)




        private void LoadLibraryIntoVm(LibraryFile lib, bool append)
        {
            int max = GuiRuntime.SlotKeys.Length;
            if (!append) _vm.LibraryRows.Clear();

            int start = _vm.LibraryRows.Count;
            foreach (var (e, idx) in lib.Entries.Select((e, i) => (e, i)))
            {
                if (start + idx >= max) break;
                _vm.LibraryRows.Add(new LibraryRow
                {
                    SlotIndex = start + idx,
                    Name = e.Name ?? "",
                    Code = e.Code ?? ""
                });
            }

            // re-index + refresh
            for (int i = 0; i < _vm.LibraryRows.Count; i++)
                _vm.LibraryRows[i].SlotIndex = i;

            UpdateTriggerLabels();
            _vm.LibraryName = string.IsNullOrWhiteSpace(lib.LibraryName) ? "Untitled Library" : lib.LibraryName;
        }



        private string GetWorkingDirOrWarn()
        {
            if (string.IsNullOrWhiteSpace(_workingDir))
            {
                System.Windows.MessageBox.Show("Please specify a Working Dir first.", "TET SOLAR",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return "";
            }
            return _workingDir;
        }

        private void OnLibraryLoad(object sender, RoutedEventArgs e)
        {
            EnsureLibrariesDir();

            var startDir = Directory.Exists(LibrariesDirFor(_workingDir))
                ? LibrariesDirFor(_workingDir)
                : _workingDir;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Library (*.json)|*.json",
                InitialDirectory = startDir
            };
            if (dlg.ShowDialog() == true)
            {
                if (!TryLoadLibraryFromPath(dlg.FileName, out var rows, out var loadedName))
                {
                    _vm.StatusText = "Load failed: invalid or empty library.";
                    return;
                }

                _vm.LibraryRows.Clear();
                for (int i = 0; i < rows.Count && i < GuiRuntime.SlotKeys.Length; i++)
                {
                    var r = rows[i];
                    r.SlotIndex = i;
                    r.TriggerLabel = TriggerLabelForIndex(i);
                    _vm.LibraryRows.Add(r);
                }

                _vm.LibraryName = loadedName ?? System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                _ = ApplyCipLogic_NoPlay();
                _vm.StatusText = $"Loaded \"{_vm.LibraryName}\".";
            }
        }


        private void OnLibraryAppend(object sender, RoutedEventArgs e)
        {
            var dir = GetWorkingDirOrWarn();
            var startDir = Directory.Exists(LibrariesDirFor(_workingDir))
        ? LibrariesDirFor(_workingDir)
        : _workingDir;
            if (string.IsNullOrEmpty(dir)) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Append Library",
                Filter = "Library JSON|*.json",
                InitialDirectory = startDir
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var text = System.IO.File.ReadAllText(dlg.FileName);
                    var lib = System.Text.Json.JsonSerializer.Deserialize<LibraryFile>(text);
                    if (lib == null) throw new Exception("Invalid library JSON.");
                    LoadLibraryIntoVm(lib, append: true);
                    _vm.StatusText = $"Appended library: {lib.LibraryName ?? System.IO.Path.GetFileNameWithoutExtension(dlg.FileName)}";
                }
                catch (Exception ex)
                {
                    _vm.StatusText = "Append failed: " + ex.Message;
                    System.Windows.MessageBox.Show(ex.Message, "Append Library Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static bool TryLoadLibraryFromPath(
    string path,
    out List<LibraryRow> rows,
    out string? libraryName)
        {
            rows = new List<LibraryRow>();
            libraryName = null;

            if (!File.Exists(path)) return false;

            try
            {
                var text = File.ReadAllText(path);

                // Preferred: LibraryFile schema
                var lib = JsonSerializer.Deserialize<LibraryFile>(text);
                if (lib != null && lib.Entries != null && lib.Entries.Count > 0)
                {
                    libraryName = lib.LibraryName;
                    int i = 0;
                    foreach (var e in lib.Entries)
                    {
                        rows.Add(new LibraryRow
                        {
                            SlotIndex = i++,
                            Name = e.Name ?? "",
                            Code = e.Code ?? ""
                        });
                    }
                    return true;
                }

                // Legacy fallback: plain List<LibraryRow>
                var legacy = JsonSerializer.Deserialize<List<LibraryRow>>(text);
                if (legacy != null && legacy.Count > 0)
                {
                    for (int i = 0; i < legacy.Count; i++)
                    {
                        var r = legacy[i];
                        r.SlotIndex = i;
                        rows.Add(r);
                    }
                    libraryName = Path.GetFileNameWithoutExtension(path);
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }



        private void OnLibrarySave(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_workingDir) || !Directory.Exists(_workingDir))
            {
                _vm.StatusText = "Please set a Working Dir first.";
                return;
            }

            EnsureWorkingLibrariesDir();
            var targetDir = LibrariesDirFor(_workingDir);

            var libName = string.IsNullOrWhiteSpace(_vm.LibraryName) ? "Library" : _vm.LibraryName.Trim();
            var safeName = string.Concat(libName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(targetDir, safeName + ".json");

            if (File.Exists(path))
            {
                var res = MessageBox.Show(
                    $"File already exists:\n{path}\n\nOverwrite?",
                    "Overwrite?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (res != MessageBoxResult.Yes)
                {
                    _vm.StatusText = "Save canceled.";
                    return;
                }
            }

            SaveLibraryToPath(path, _vm.LibraryName);
            _vm.StatusText = $"Library saved → {path}";
        }





        void WriteAt(char[] line, int start, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            int n = Math.Min(s.Length, Math.Max(0, line.Length - start));
            for (int i = 0; i < n; i++) line[start + i] = s[i];
        }

        string TruncatePadRight(string s, int width)
        {
            if (width <= 0) return "";
            if (s.Length > width) return s.Substring(0, width - 1) + "…";
            if (s.Length < width) return s + new string(' ', width - s.Length);
            return s;
        }

        void AppendLiveLine(string text)
        {
            // append and auto-scroll; cap lines
            LiveBox.AppendText(text + Environment.NewLine);

            // cap
            var lines = LiveBox.LineCount;
            if (lines > _liveMaxLines)
            {
                // remove top chunk roughly (fast path)
                LiveBox.Select(0, LiveBox.GetCharacterIndexFromLineIndex(Math.Min(lines - _liveMaxLines, lines - 1)));
                LiveBox.SelectedText = "";
                LiveBox.CaretIndex = LiveBox.Text.Length;
            }

            LiveBox.ScrollToEnd();
        }
        private void SyncLiveTimerInterval()
        {
            double bpm = Math.Clamp(_tempoBpm, 10.0, 300.0);
            double ms = 60000.0 / (bpm * 8.0);
            _liveTimer!.Interval = TimeSpan.FromMilliseconds(ms);
        }



        // ==== Zoom buttons ====
        private void OnZoomMinus(object? sender, RoutedEventArgs e)
        {
            _liveFontSize = Math.Max(8, _liveFontSize - 1);
            if (LiveBox != null) LiveBox.FontSize = _liveFontSize;
        }

        private void OnZoomPlus(object? sender, RoutedEventArgs e)
        {
            _liveFontSize = Math.Min(48, _liveFontSize + 1);
            if (LiveBox != null) LiveBox.FontSize = _liveFontSize;
        }






        private void OnLiveTick(object? sender, EventArgs e)
        {
            // 1) LEFT/MIDDLE (names/markers): one line per tick
            if (_liveChordJustChanged)
            {
                // print the chord name once at the moment of change
                AppendNamesLine(_liveCurrentName);
            }
            else if (_liveMarker.HasValue)
            {
                // + / - / R one-shot marker
                AppendNamesLine(_liveMarker.Value.ToString());
                _liveMarker = null;
            }
            else
            {
                // idle tick line (keeps vertical alignment with field on the right)
                AppendNamesLine("");
            }

            // 2) MIDDLE/LEFT (shortcode + pcs): print ONLY ON CHANGE, and never duplicate identical retriggers
            if (_liveChordJustChanged)
            {
                var pcsText = _liveCurrentPcs.Count > 0 ? string.Join(", ", _liveCurrentPcs) : "";
                var code = _rt.GetCurrentShortcode();
                var sig = $"{_liveCurrentName}|{code}|{pcsText}";

                if (!string.Equals(sig, _lastPrintedChordSig, StringComparison.Ordinal))
                {
                    AppendLine(ShortcodeBox, $"{_liveCurrentName}\n{code}:\n{pcsText}");
                    AppendLine(ShortcodeBox, "");   // ← add one empty line after the block
                    _lastPrintedChordSig = sig;
                }
            }


            // 3) RIGHT (field): pipes when playing, otherwise idle marker
            if (_rt.IsPlaying && _liveCurrentPcs.Count > 0)
                AppendFieldLine(BuildPipeFieldLine(_liveCurrentPcs, LiveBox));
            else
                AppendFieldLine(BuildIdleTickLine(LiveBox));

            _liveChordJustChanged = false;
        }


        // Build a pipe field (right column)
        private string BuildPipeFieldLine(IReadOnlyList<int> pcs, System.Windows.Controls.TextBox target)
        {
            int cols = EstimatedColumnsFor(target);
            char[] line = new string(' ', cols).ToCharArray();

            int fieldCols = cols; // full width now; we’re in a dedicated box
            var used = new bool[fieldCols];

            foreach (var pc in pcs)
            {
                int col = MapPcToFieldCol(pc, fieldCols);
                if ((uint)col >= (uint)fieldCols) continue;
                if (used[col]) continue;
                used[col] = true;
                line[col] = '|';
            }
            return new string(line);
        }

        // Idle tick line with a black square in the horizontal center
        private string BuildIdleTickLine(System.Windows.Controls.TextBox target)
        {
            int cols = EstimatedColumnsFor(target);
            char[] line = new string(' ', cols).ToCharArray();
            int c = Math.Max(0, Math.Min(cols - 1, cols / 2));
            line[c] = TickSquare;
            return new string(line);
        }

        // Estimated columns for monospaced box
        private static int EstimatedColumnsFor(System.Windows.Controls.TextBox box)
        {
            double charW = Math.Max(6.0, box.FontSize * 0.6);
            double px = Math.Max(200.0, box.ActualWidth);
            int cols = (int)Math.Floor(px / charW);
            return Math.Max(40, cols);
        }


        private static int MapPcToFieldCol(int pc, int fieldCols)
        {
            pc = Math.Clamp(pc, 1, 248);
            return (int)Math.Round((pc - 1) * (fieldCols - 1) / 247.0);
        }

        private void AppendNamesLine(string text)
        {
            AppendLine(NamesBox, text);
            TrimTopIfTooLong(NamesBox);
            NamesBox.ScrollToEnd();
        }


        private void AppendFieldLine(string text)
        {
            AppendLine(LiveBox, text);
            TrimTopIfTooLong(LiveBox);
            LiveBox.ScrollToEnd();
        }

        // Trim scrollback to _liveMaxLines
        private void TrimTopIfTooLong(System.Windows.Controls.TextBox box)
        {
            int lines = box.LineCount;
            if (lines <= _liveMaxLines) return;
            int dropTo = Math.Min(lines - _liveMaxLines, lines - 1);
            int charEnd = box.GetCharacterIndexFromLineIndex(dropTo);
            box.Select(0, charEnd);
            box.SelectedText = string.Empty;
            box.CaretIndex = box.Text.Length;
        }

        private static void AppendLine(System.Windows.Controls.TextBox box, string line, int maxLines = 200000)
        {
            if (string.IsNullOrWhiteSpace(line)) line = "";
            box.AppendText(line + Environment.NewLine);

            // trim scrollback
            int lines = box.LineCount;
            if (lines > maxLines)
            {
                int dropTo = Math.Min(lines - maxLines, lines - 1);
                int charEnd = box.GetCharacterIndexFromLineIndex(dropTo);
                box.Select(0, charEnd);
                box.SelectedText = string.Empty;
                box.CaretIndex = box.Text.Length;
            }
            box.ScrollToEnd();
        }

        private int _lastAppliedCount = 0;

        private int ApplyCipLogic_NoPlay()
        {
            var rows = _vm.LibraryRows
                          .OrderBy(r => r.SlotIndex)
                          .Select(r => (name: string.IsNullOrWhiteSpace(r.Name) ? null : r.Name,
                                        codeOrPcs: r.Code))
                          .ToList();

            _rt.LoadSlotsFromRows(rows);

            // Count only usable rows (non-empty Code)
            _lastAppliedCount = rows.Count(r => !string.IsNullOrWhiteSpace(r.codeOrPcs));
            return _lastAppliedCount;
        }


        // Attach a SINGLE key handler to the editor TextBox; detach before attach to avoid stacking
        private void LibraryGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
        {
            if (e.EditingElement is System.Windows.Controls.TextBox tb)
            {
                tb.PreviewKeyDown -= CellEditor_PreviewKeyDown;
                tb.PreviewKeyDown += CellEditor_PreviewKeyDown;
            }
        }

        // Centralized editor key logic: Enter commits & applies; Tab navigates deterministically
        private void CellEditor_PreviewKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
        {
            // Safety: must have a current cell/row/column
            if (LibraryGrid.CurrentCell.Column == null)
                return;

            const int KEY_COL = 0; // read-only "Trigger" column
            const int NAME_COL = 1;
            const int CODE_COL = 2;

            // ENTER: commit & apply; restore global shortcuts
            if (e.Key == Key.Enter)
            {
                TryCommitEdits();

                // Apply after commit, outside edit pipeline
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnApplyCip(null, new RoutedEventArgs());
                    FocusWindow();
                }), System.Windows.Threading.DispatcherPriority.Background);

                e.Handled = true; // prevent newline
                return;
            }

            // TAB: custom navigation (commit first, then move)
            if (e.Key == Key.Tab)
            {
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                // Commit current edit BEFORE changing cell — prevents re-entrancy
                TryCommitEdits();

                int colIndex = LibraryGrid.Columns.IndexOf(LibraryGrid.CurrentCell.Column);
                int rowIndex = LibraryGrid.Items.IndexOf(LibraryGrid.CurrentItem);
                if (rowIndex < 0) { e.Handled = true; return; }

                if (!shift)
                {
                    // Name -> Code (same row)
                    if (colIndex == NAME_COL)
                    {
                        e.Handled = true;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            LibraryGrid.CurrentCell = new DataGridCellInfo(LibraryGrid.Items[rowIndex], LibraryGrid.Columns[CODE_COL]);
                            LibraryGrid.BeginEdit();
                        }));
                        return;
                    }

                    // Code -> Name (next row); create next row if needed & available
                    if (colIndex == CODE_COL)
                    {
                        e.Handled = true;

                        int targetRow = rowIndex + 1;
                        if (targetRow >= _vm.LibraryRows.Count)
                        {
                            if (_vm.LibraryRows.Count >= GuiRuntime.SlotKeys.Length)
                            {
                                _vm.StatusText = "All slots assigned";
                                return;
                            }

                            // Create new row (no TriggerLabel here; UpdateTriggerLabels() will refresh)
                            int idx = _vm.LibraryRows.Count;
                            _vm.LibraryRows.Add(new LibraryRow
                            {
                                SlotIndex = idx,
                                Name = "",
                                Code = ""
                            });
                            UpdateTriggerLabels();
                        }

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            int r = Math.Min(rowIndex + 1, _vm.LibraryRows.Count - 1);
                            LibraryGrid.SelectedIndex = r;
                            LibraryGrid.CurrentCell = new DataGridCellInfo(LibraryGrid.Items[r], LibraryGrid.Columns[NAME_COL]);
                            LibraryGrid.BeginEdit();
                        }));
                        return;
                    }

                    // If somehow in KEY_COL, skip to NAME same row
                    if (colIndex == KEY_COL)
                    {
                        e.Handled = true;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            LibraryGrid.CurrentCell = new DataGridCellInfo(LibraryGrid.Items[rowIndex], LibraryGrid.Columns[NAME_COL]);
                            LibraryGrid.BeginEdit();
                        }));
                        return;
                    }
                }
                else
                {
                    // SHIFT+TAB → let DataGrid handle default backwards nav (we already committed)
                    e.Handled = false;
                    return;
                }
            }

            // Ctrl+N inside editor: commit and add a new row; edit Name
            if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                TryCommitEdits();
                AddNewRowAndEditName();
                e.Handled = true;
                return;
            }
        }

        private void TryCommitEdits()
        {
            try { LibraryGrid.CommitEdit(DataGridEditingUnit.Cell, true); } catch { }
            try { LibraryGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }
        }





        // Lets the DataGrid pass global shortcuts (space, +/-, slot keys, etc.)
        // to your existing Window-level handler when you're NOT editing a cell.
        private void LibraryGrid_PreviewKeyDown_Global(object? sender, System.Windows.Input.KeyEventArgs e)
        {
            // If a TextBox editor is open, let the cell-editor handler do its job.
            if (e.OriginalSource is System.Windows.Controls.TextBox)
                return;

            // Forward to the same logic you use for global shortcuts.
            OnWindowPreviewKeyDown(sender!, e);
        }


        private void SaveLibraryToPath(string path, string? libraryName = null)
        {
            var snapshot = new LibraryFile
            {
                LibraryName = string.IsNullOrWhiteSpace(libraryName) ? null : libraryName!.Trim(),
                Entries = _vm.LibraryRows
                             .OrderBy(r => r.SlotIndex)
                             .Where(r => !string.IsNullOrWhiteSpace(r.Code))
                             .Select(r => new LibraryEntry { Name = r.Name ?? "", Code = r.Code!.Trim() })
                             .ToList()
            };

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, _jsonIndented));
        }


        private void TryLoadLibraryFromWorkingDir()
        {
            try
            {
                var dir = LibrariesDirFor(_workingDir);
                if (!Directory.Exists(dir)) return;

                // Pick newest *.json in the working dir's libraries folder
                var newest = new DirectoryInfo(dir)
                    .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest == null) return;

                if (!TryLoadLibraryFromPath(newest.FullName, out var rows, out var loadedName))
                    return;

                _vm.LibraryRows.Clear();
                for (int i = 0; i < rows.Count && i < GuiRuntime.SlotKeys.Length; i++)
                {
                    var r = rows[i];
                    r.SlotIndex = i;
                    r.TriggerLabel = TriggerLabelForIndex(i);
                    _vm.LibraryRows.Add(r);
                }

                _vm.LibraryName = loadedName ?? Path.GetFileNameWithoutExtension(newest.Name);
                _ = ApplyCipLogic_NoPlay();
                _vm.StatusText = $"Loaded library from working dir: \"{_vm.LibraryName}\".";
            }
            catch (Exception ex)
            {
                _vm.StatusText = "Auto-load library failed: " + ex.Message;
            }
        }






    } //Main Window
}