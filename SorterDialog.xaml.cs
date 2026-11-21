using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace ImageSorter;

/// <summary>Name a sorter, point it at a folder and choose the key that sends pictures there.</summary>
public partial class SorterDialog : Window
{
    private readonly List<Sorter> _others;
    private bool _capturing;

    public SorterDialog(Window owner, string title, string name, string folder, Key? hotkey, IEnumerable<Sorter> others)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        _others = others.ToList();
        Hotkey = hotkey;

        NameBox.Text = name;
        FolderBox.Text = folder;
        ShowKey();

        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string SorterName => NameBox.Text.Trim();
    public string Folder => FolderBox.Text.Trim();
    public Key? Hotkey { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Pick the folder to sort into" };
        if (Directory.Exists(Folder)) picker.InitialDirectory = Folder;
        if (picker.ShowDialog(this) != true) return;

        // Keep a name the user typed; only follow the folder if the name was still the old folder's.
        bool nameFollowsFolder = SorterName.Length == 0
                                 || (Folder.Length > 0 && SorterName == new DirectoryInfo(Folder).Name);
        FolderBox.Text = picker.FolderName;
        if (nameFollowsFolder) NameBox.Text = new DirectoryInfo(picker.FolderName).Name;
    }

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        KeyButton.Content = "Press a key…";
        KeyNote.Text = "Numbers, letters, F-keys and punctuation all work. Esc cancels.";
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        _capturing = false;
        Hotkey = null;
        ShowKey();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        Key key = KeyNames.Normalize(e.Key == Key.System ? e.SystemKey : e.Key);
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;   // wait for the real key

        if (key == Key.Escape)
        {
            _capturing = false;
            ShowKey();
            return;
        }
        if (!KeyNames.IsAssignable(key))
        {
            KeyNote.Text = "That key is used for browsing. Pick a number, a letter, an F-key or punctuation.";
            return;
        }

        Hotkey = key;
        _capturing = false;
        ShowKey();
    }

    private void ShowKey()
    {
        if (Hotkey is not { } key)
        {
            KeyButton.Content = "Set a hotkey";
            KeyNote.Text = "Without a hotkey you can still click the sorter to send a picture there.";
            return;
        }

        string label = KeyNames.Label(key);
        KeyButton.Content = $"{label}   (click to change)";
        Sorter? clash = _others.FirstOrDefault(s => s.Key == key);
        KeyNote.Text = clash is null
            ? $"Pressing {label} sends the current picture to this folder."
            : $"{label} belongs to {clash.Name} right now. Saving moves it here and leaves {clash.Name} without a hotkey.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SorterName.Length == 0)
        {
            ShowError("Give the sorter a name.");
            return;
        }
        if (Folder.Length == 0)
        {
            ShowError("Pick a folder to sort into.");
            return;
        }
        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex)
        {
            ShowError($"That folder can't be used. {ex.Message}");
            return;
        }
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
