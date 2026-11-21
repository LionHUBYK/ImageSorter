using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageSorter;

public enum SortMode { Move, Copy }
public enum SortOrder { Name, Oldest, Newest }
public enum MediaKind { Image, Heif, Raw, Video }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>One file waiting to be sorted. Its thumbnail loads the first time something asks for it.</summary>
public sealed class ImageItem : Observable
{
    private ImageSource? _thumb;
    private bool _requested;

    private string? _folderLabel;

    public ImageItem(string path, MediaKind kind, string folderKey = "")
    {
        Path = path;
        Kind = kind;
        FolderKey = folderKey;
    }

    public string Path { get; }
    public MediaKind Kind { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsVideo => Kind == MediaKind.Video;

    /// <summary>This file's folder relative to the folder that was opened; empty for the opened folder itself.</summary>
    public string FolderKey { get; }

    /// <summary>The name including any subfolders, e.g. <c>Trips\2024\IMG_1.jpg</c>.</summary>
    public string RelativeName => FolderKey.Length == 0 ? Name : System.IO.Path.Combine(FolderKey, Name);

    /// <summary>
    /// Set on the first picture of each subfolder, so the filmstrip can mark where that folder's pictures start.
    /// </summary>
    public string? FolderLabel
    {
        get => _folderLabel;
        set => Set(ref _folderLabel, value);
    }

    public ImageSource? Thumb
    {
        get
        {
            if (_thumb is null && !_requested)
            {
                _requested = true;
                ThumbService.Request(this);
            }
            return _thumb;
        }
    }

    /// <summary>The thumbnail if it is already in memory; never triggers a load.</summary>
    public ImageSource? ThumbIfLoaded => _thumb;

    internal void SetThumb(ImageSource? thumb)
    {
        _thumb = thumb;
        Raise(nameof(Thumb));
    }

    /// <summary>Forget the thumbnail so it can be loaded again the next time it scrolls into view.</summary>
    internal void DropThumb()
    {
        _thumb = null;
        _requested = false;
    }

    /// <summary>The load was dropped from a full queue. A cell that is still on screen asks again.</summary>
    internal void CancelRequest()
    {
        _requested = false;
        Raise(nameof(Thumb));
    }
}

/// <summary>A small picture inside a sorter's card. Created empty, then filled when the flying image lands.</summary>
public sealed class BinThumb : Observable
{
    private ImageSource? _image;

    public BinThumb(string name) => Name = name;

    public string Name { get; }

    public ImageSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }
}

/// <summary>A destination folder with a name and a hotkey.</summary>
public sealed class Sorter : Observable
{
    private string _name;
    private string _folder;
    private Key? _key;
    private int _count;

    public Sorter(string name, string folder, Key? key)
    {
        _name = name;
        _folder = folder;
        _key = key;
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Folder
    {
        get => _folder;
        set => Set(ref _folder, value);
    }

    public Key? Key
    {
        get => _key;
        set
        {
            if (!Set(ref _key, value)) return;
            Raise(nameof(KeyLabel));
            Raise(nameof(EmptyHint));
        }
    }

    public string KeyLabel => _key is { } k ? KeyNames.Label(k) : "–";

    public string EmptyHint => _key is { } k ? $"Press {KeyNames.Label(k)} to send here" : "Click to send here";

    /// <summary>How many files went here this session.</summary>
    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    public ObservableCollection<BinThumb> Recent { get; } = new();
}

public static class KeyNames
{
    /// <summary>Number pad digits count as the same hotkey as the top-row digits.</summary>
    public static Key Normalize(Key key) =>
        key is >= Key.NumPad0 and <= Key.NumPad9 ? Key.D0 + (key - Key.NumPad0) : key;

    /// <summary>Keys a sorter may use. Arrows, Space, Tab, Esc and friends stay reserved for the app.</summary>
    public static bool IsAssignable(Key key) => key is
        (>= Key.D0 and <= Key.D9) or (>= Key.A and <= Key.Z) or (>= Key.F1 and <= Key.F12)
        or Key.OemComma or Key.OemPeriod or Key.OemQuestion or Key.OemSemicolon or Key.OemQuotes
        or Key.OemOpenBrackets or Key.OemCloseBrackets or Key.OemMinus or Key.OemPlus
        or Key.OemTilde or Key.OemPipe;

    public static string Label(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemTilde => "`",
        Key.OemPipe => "\\",
        _ => key.ToString(),
    };

    public static Key? Parse(string? text) =>
        Enum.TryParse(text, out Key key) && IsAssignable(key) ? key : null;
}

public sealed class SorterConfig
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? Key { get; set; }
}

public sealed class AppSettings
{
    public List<SorterConfig> Sorters { get; set; } = new();
    public string? LastFolder { get; set; }
    public bool IncludeSubfolders { get; set; }
    public SortMode Mode { get; set; } = SortMode.Move;
    public SortOrder Order { get; set; } = SortOrder.Name;
    public bool Muted { get; set; } = true;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool Maximized { get; set; }
    public double BinsWidth { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// A settings file sitting next to the .exe wins (portable use, e.g. on a USB stick);
    /// otherwise settings live in the user's AppData.
    /// </summary>
    private static string FilePath
    {
        get
        {
            string portable = Path.Combine(AppContext.BaseDirectory, "ImageSorter.settings.json");
            if (File.Exists(portable)) return portable;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "ImageSorter", "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return new AppSettings();
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Losing a settings write is not worth interrupting a sorting session.
        }
    }
}
