using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TetSolar.GUI.ViewModels
{
    public sealed class LibraryRow : INotifyPropertyChanged
    {
        int _slotIndex;
        string _triggerLabel = "";
        string _name = "";
        string _code = "";

        public int SlotIndex
        {
            get => _slotIndex;
            set { if (value != _slotIndex) { _slotIndex = value; OnPropertyChanged(); } }
        }

        public string TriggerLabel
        {
            get => _triggerLabel;
            set { if (value != _triggerLabel) { _triggerLabel = value; OnPropertyChanged(); } }
        }

        public string Name
        {
            get => _name;
            set { if (value != _name) { _name = value; OnPropertyChanged(); } }
        }

        public string Code
        {
            get => _code;
            set { if (value != _code) { _code = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
