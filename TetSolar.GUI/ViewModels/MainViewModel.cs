using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TetSolar.GUI.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<LibraryRow> LibraryRows { get; } = new();

        string _statusText = "Ready";
        string _midiOutLabel = "(no device)";
        string _midiInLabel = "(off)";
        string _pbLabel = "PB: ±2";
        string _transposeLabel = "Transpose: +0";
        string _midiFilePath = "";

        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
        public string MidiOutLabel { get => _midiOutLabel; set { _midiOutLabel = value; OnPropertyChanged(); } }
        public string MidiInLabel { get => _midiInLabel; set { _midiInLabel = value; OnPropertyChanged(); } }
        public string PbLabel { get => _pbLabel; set { _pbLabel = value; OnPropertyChanged(); } }
        public string TransposeLabel { get => _transposeLabel; set { _transposeLabel = value; OnPropertyChanged(); } }
        public string MidiFilePath { get => _midiFilePath; set { _midiFilePath = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private string _libraryName = "MyLibrary";
        public string LibraryName
        {
            get => _libraryName;
            set { if (value != _libraryName) { _libraryName = value; OnPropertyChanged(); } }
        }
        void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
