using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfAnimatedGif;

namespace ImageSorter;

public partial class MainWindow : Window
{
    private const double FilmCell = 118;   // filmstrip cell width incl. margins; keep in step with the FilmItem style
    private const int MaxRecent = 16;      // pictures remembered per sorter card
    private const double BinThumbSize = 52;
    private const int FlyMilliseconds = 330;

    private static readonly Color AccentColor = Color.FromRgb(0x60, 0xCD, 0xFF);
    private static readonly Color StrokeColor = Color.FromRgb(0x38, 0x38, 0x38);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<Sorter> _sorters = new();
    private readonly List<SortOp> _undo = new();
    private readonly PreviewCache _previews = new();
    private readonly DispatcherTimer _videoTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private ObservableCollection<ImageItem> _items = new();
    private string? _folder;
    private int _index = -1;
    private bool _ready;

    private int _scanVersion;
    private int _showVersion;
    private DecodedImage? _shown;      // what MainImage is currently displaying
    private ImageItem? _shownItem;
    private int _shownSide;            // the size the current preview was decoded for
    private bool _fullRequested;
    private bool _animating;

    private bool _syncingFilmstrip;
    private ScrollViewer? _filmScroll;
    private bool _filmNeedsSettle;     // the list was just replaced; scroll again once it has been measured
    private bool _labelFolders;        // show a badge where each subfolder's pictures start

    private bool _panning;
    private Point _panFrom;
    private Point _panOrigin;

    private bool _playing;
    private bool _syncingSlider;

    // File work runs one job at a time, in order, off the UI thread.
    private Task _ioTail = Task.CompletedTask;
    private int _ioPending;

    public MainWindow()
    {
        InitializeComponent();
        ThumbService.Init(Dispatcher);
        ImageBehavior.SetRepeatBehavior(MainImage, RepeatBehavior.Forever);
        _videoTimer.Tick += VideoTimer_Tick;
        _resizeTimer.Tick += (_, _) => Resharpen();
        ViewerHost.SizeChanged += (_, _) =>
        {
            // Wait until the resize settles before decoding anything.
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };

        BinsList.ItemsSource = _sorters;
        Filmstrip.ItemsSource = _items;
        ApplySettings();
        _ready = true;

        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        Loaded += async (_, _) =>
        {
            string? start = Environment.GetCommandLineArgs().Skip(1)
                                .FirstOrDefault(a => Directory.Exists(a) || File.Exists(a))
                            ?? _settings.LastFolder;
            if (start is not null && (Directory.Exists(start) || File.Exists(start))) await OpenPath(start);
            else ShowCurrent();
        };
    }

    private ImageItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    private SortMode Mode => CopySegment.IsChecked == true ? SortMode.Copy : SortMode.Move;

    // ------------------------------------------------------------------ settings

    private void ApplySettings()
    {
        if (_settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
        {
            Width = Math.Min(_settings.WindowWidth, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(_settings.WindowHeight, SystemParameters.VirtualScreenHeight);
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
        if (_settings.BinsWidth is >= 270 and <= 720) BinsColumn.Width = new GridLength(_settings.BinsWidth);

        foreach (SorterConfig config in _settings.Sorters)
            _sorters.Add(new Sorter(config.Name, config.Folder, KeyNames.Parse(config.Key)));

        SubfoldersCheck.IsChecked = _settings.IncludeSubfolders;
        OrderCombo.SelectedIndex = (int)_settings.Order;
        (_settings.Mode == SortMode.Copy ? CopySegment : MoveSegment).IsChecked = true;
        BinsEmpty.Visibility = _sorters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveSettings()
    {
        _settings.Sorters = _sorters
            .Select(s => new SorterConfig { Name = s.Name, Folder = s.Folder, Key = s.Key?.ToString() })
            .ToList();
        _settings.LastFolder = _folder;
        _settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        _settings.Order = (SortOrder)Math.Max(0, OrderCombo.SelectedIndex);
        _settings.Mode = Mode;
        _settings.Maximized = WindowState == WindowState.Maximized;
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.BinsWidth = BinsColumn.ActualWidth;
        _settings.Save();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopVideo();
        SaveSettings();
        if (_ioPending > 0)
        {
            Title = "Image Sorter – finishing file moves…";
            _ioTail.Wait(TimeSpan.FromSeconds(60));
        }
    }

    // ------------------------------------------------------------------ opening folders

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Pick the folder you want to sort" };
        if (_folder is not null && Directory.Exists(_folder)) picker.InitialDirectory = _folder;
        if (picker.ShowDialog(this) == true) await LoadFolder(picker.FolderName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) await OpenPath(paths[0]);
    }

    /// <summary>Open a folder, or the folder a file lives in with that file selected.</summary>
    private async Task OpenPath(string path)
    {
        if (Directory.Exists(path)) await LoadFolder(path);
        else if (File.Exists(path) && Path.GetDirectoryName(path) is { } parent) await LoadFolder(parent, path);
    }

    private async Task LoadFolder(string folder, string? select = null, bool keepPosition = false)
    {
        int version = ++_scanVersion;
        string? keep = select ?? (keepPosition ? Current?.Path : null);
        bool differentFolder = !SamePath(folder, _folder);

        _folder = folder;
        PathText.Text = folder;
        Title = $"Image Sorter – {new DirectoryInfo(folder).Name}";
        SetStatus("Looking for pictures…");

        bool recurse = SubfoldersCheck.IsChecked == true;
        var order = (SortOrder)Math.Max(0, OrderCombo.SelectedIndex);
        // With subfolders on, don't offer files that already sit in one of the destinations.
        List<string> skip = _sorters.Select(s => s.Folder).Where(f => IsInside(f, folder)).ToList();

        List<ImageItem> found;
        try
        {
            await _ioTail;   // let queued moves land first so they don't show up again
            found = await Task.Run(() => Scan(folder, recurse, order, skip));
        }
        catch (Exception ex)
        {
            if (version == _scanVersion) SetStatus($"Couldn't read that folder. {ex.Message}", isError: true);
            return;
        }
        if (version != _scanVersion) return;

        if (differentFolder) _undo.Clear();
        _previews.Clear();
        _items = new ObservableCollection<ImageItem>(found);
        _labelFolders = order == SortOrder.Name;   // date orders mix folders together, so badges would be noise
        for (int i = 0; i < _items.Count; i++) RefreshFolderLabel(i);
        Filmstrip.ItemsSource = _items;
        _filmNeedsSettle = true;
        _index = _items.Count == 0 ? -1 : 0;
        if (keep is not null)
        {
            int at = IndexOfPath(keep);
            if (at >= 0) _index = at;
        }

        SetStatus(_items.Count == 0
            ? "No pictures or videos found here."
            : $"{_items.Count:N0} {(_items.Count == 1 ? "file" : "files")} to sort.");
        ShowCurrent();
        SaveSettings();
    }

    private static List<ImageItem> Scan(string folder, bool recurse, SortOrder order, List<string> skip)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var found = new List<(string Path, MediaKind Kind, DateTime Time, string Folder, string Name)>();
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            if (ImageDecoder.Classify(file.Name) is not { } kind) continue;
            string? dir = file.DirectoryName;
            if (recurse && skip.Count > 0 && dir is not null
                && skip.Any(s => SamePath(dir, s) || IsInside(dir, s))) continue;

            string relative = recurse && dir is not null ? Path.GetRelativePath(folder, dir) : ".";
            found.Add((file.FullName, kind, order == SortOrder.Name ? default : file.LastWriteTimeUtc,
                relative == "." ? "" : relative, file.Name));
        }

        switch (order)
        {
            case SortOrder.Oldest: found.Sort((a, b) => a.Time.CompareTo(b.Time)); break;
            case SortOrder.Newest: found.Sort((a, b) => b.Time.CompareTo(a.Time)); break;
            default:
                // Folder by folder, all the way down: a folder's own pictures first, then each of its
                // subfolders in turn (and their subfolders), so one folder's pictures always sit together.
                found.Sort((a, b) =>
                {
                    int byFolder = CompareFolders(a.Folder, b.Folder);
                    return byFolder != 0 ? byFolder : Native.StrCmpLogicalW(a.Name, b.Name);
                });
                break;
        }
        return found.Select(f => new ImageItem(f.Path, f.Kind, f.Folder)).ToList();
    }

    /// <summary>Orders relative folders so a parent comes before its children and siblings sort like Explorer.</summary>
    private static int CompareFolders(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return -1;
        if (b.Length == 0) return 1;
        string[] x = a.Split(Path.DirectorySeparatorChar), y = b.Split(Path.DirectorySeparatorChar);
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int c = Native.StrCmpLogicalW(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>Puts the folder badge on whichever picture is currently the first one of its subfolder.</summary>
    private void RefreshFolderLabel(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        ImageItem item = _items[i];
        bool startsFolder = _labelFolders && item.FolderKey.Length > 0
                            && (i == 0 || _items[i - 1].FolderKey != item.FolderKey);
        item.FolderLabel = startsFolder ? Path.GetFileName(item.FolderKey) : null;
    }

    private async void Subfolders_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_folder is null) SaveSettings();
        else await LoadFolder(_folder, keepPosition: true);
    }

    private async void Order_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (_folder is null) SaveSettings();
        else await LoadFolder(_folder, keepPosition: true);
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SetStatus(Mode == SortMode.Move
            ? "Move: pictures leave this folder when you sort them."
            : "Copy: pictures stay here and a copy goes to the sorter.");
        SaveSettings();
    }

    // ------------------------------------------------------------------ keyboard

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (OrderCombo.IsDropDownOpen) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.Control)
        {
            if (key == Key.Z) { Undo(); e.Handled = true; }
            else if (key == Key.O) { OpenFolder_Click(this, new RoutedEventArgs()); e.Handled = true; }
            return;
        }
        if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift) return;

        switch (key)
        {
            case Key.Left or Key.Up or Key.PageUp:
                Step(-1);
                break;
            case Key.Right or Key.Down or Key.PageDown or Key.Space:
                Step(+1);
                break;
            case Key.Home:
                GoTo(0);
                break;
            case Key.End:
                GoTo(_items.Count - 1);
                break;
            case Key.Tab:
                if (!e.IsRepeat) (Mode == SortMode.Move ? CopySegment : MoveSegment).IsChecked = true;
                break;
            case Key.Escape:
                ResetZoom();
                break;
            default:
                Key hotkey = KeyNames.Normalize(key);
                Sorter? sorter = _sorters.FirstOrDefault(s => s.Key == hotkey);
                if (sorter is null) return;
                // A held-down key must not machine-gun a whole folder into one place.
                if (!e.IsRepeat) SortTo(sorter);
                break;
        }
        e.Handled = true;
    }

    private void Step(int delta)
    {
        if (_items.Count == 0) return;
        int next = Math.Clamp(_index + delta, 0, _items.Count - 1);
        if (next == _index)
        {
            SetStatus(delta > 0 ? "That's the last one." : "That's the first one.");
            return;
        }
        _index = next;
        ShowCurrent();
    }

    private void GoTo(int index)
    {
        if (_items.Count == 0) return;
        _index = Math.Clamp(index, 0, _items.Count - 1);
        ShowCurrent();
    }

    // ------------------------------------------------------------------ showing the current file

    private async void ShowCurrent()
    {
        int version = ++_showVersion;
        StopVideo();
        ResetZoom();
        _shown = null;
        _shownItem = null;
        _fullRequested = false;
        MainImage.MaxWidth = MainImage.MaxHeight = double.PositiveInfinity;
        ProblemText.Visibility = Visibility.Collapsed;
        UpdateChrome();
        SyncFilmstrip();

        ImageItem? item = Current;
        if (item is null)
        {
            SetStill(null);
            NamePill.Visibility = Visibility.Collapsed;
            ShowEmptyState();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        NameText.Text = item.RelativeName;   // includes the subfolder, so you can tell where a picture lives
        NamePill.Visibility = Visibility.Visible;

        if (item.IsVideo)
        {
            SetStill(null);
            StartVideo(item);
            Prefetch();
            return;
        }

        int side = PreviewSide();
        Task<DecodedImage?> loading = _previews.Get(item.Path, side);
        Prefetch();
        if (!loading.IsCompleted) SetStill(item.ThumbIfLoaded);   // something to look at meanwhile

        DecodedImage? decoded = await loading;
        if (version != _showVersion) return;   // the user has already moved on

        if (decoded is null)
        {
            SetStill(item.ThumbIfLoaded);
            if (item.ThumbIfLoaded is null) ShowProblem("This file can't be previewed, but you can still sort it.");
            return;
        }

        _shown = decoded;
        _shownItem = item;
        _shownSide = side;

        // The picture on screen must never be a blank square in the filmstrip: if its own thumbnail
        // hasn't loaded (or failed), make one from what was just decoded.
        if (item.ThumbIfLoaded is null) item.SetThumb(ImageDecoder.Downscale(decoded.Bitmap, ThumbService.Side));

        // A small picture is shown at its real size instead of being stretched into a blur.
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        MainImage.MaxWidth = Math.Max(1, decoded.FullWidth / dpi);
        MainImage.MaxHeight = Math.Max(1, decoded.FullHeight / dpi);

        if (decoded.GifData is { } gif) SetAnimated(gif);
        else SetStill(decoded.Bitmap);
    }

    /// <summary>After the window grows, decode the current picture again at the new size so it stays sharp.</summary>
    private async void Resharpen()
    {
        _resizeTimer.Stop();
        if (_shown is not { IsFull: false, GifData: null } || _shownItem is not { } item || item != Current) return;
        int side = PreviewSide();
        if (side <= _shownSide) return;

        int version = _showVersion;
        DecodedImage? sharper = await _previews.Get(item.Path, side);
        if (version != _showVersion || sharper is null) return;
        _shown = sharper;
        _shownSide = side;
        MainImage.Source = sharper.Bitmap;
    }

    private void ShowEmptyState()
    {
        if (_folder is null)
        {
            EmptyTitle.Text = "Pick a folder to start";
            EmptyBody.Text = "Open a folder of pictures, add a few sorters on the right, then press their keys to file each one.";
        }
        else if (_undo.Count > 0)
        {
            EmptyTitle.Text = "Nothing left to sort";
            EmptyBody.Text = "Every picture in this folder has been filed. Open another folder to keep going.";
        }
        else
        {
            EmptyTitle.Text = "No pictures here";
            EmptyBody.Text = "This folder has no pictures or videos. Try another folder, or turn on Include subfolders.";
        }
        EmptyState.Visibility = Visibility.Visible;
    }

    private void ShowProblem(string message)
    {
        ProblemText.Text = message;
        ProblemText.Visibility = Visibility.Visible;
    }

    private void SetStill(ImageSource? source)
    {
        if (_animating)
        {
            ImageBehavior.SetAnimatedSource(MainImage, null);
            _animating = false;
        }
        MainImage.Source = source;
    }

    private void SetAnimated(byte[] gif)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(gif);
        image.EndInit();
        ImageBehavior.SetAnimatedSource(MainImage, image);
        _animating = true;
    }

    /// <summary>Decode the neighbours now so the next keypress shows a picture instantly.</summary>
    private void Prefetch()
    {
        int side = PreviewSide();
        foreach (int offset in (ReadOnlySpan<int>)[1, 2, -1])
        {
            int i = _index + offset;
            if (i >= 0 && i < _items.Count && !_items[i].IsVideo) _previews.Get(_items[i].Path, side);
        }
    }

    /// <summary>How many pixels the preview needs to look sharp in the viewer at its current size.</summary>
    private int PreviewSide()
    {
        double side = Math.Max(ViewerHost.ActualWidth, ViewerHost.ActualHeight) * VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (side < 100) side = 1600;
        return (int)Math.Clamp(Math.Ceiling(side / 256) * 256, 1024, 4096);
    }

    private void UpdateChrome()
    {
        string counter = _items.Count == 0 ? "" : $"{_index + 1:N0} of {_items.Count:N0}";
        if (_ioPending > 0) counter += $"   filing {_ioPending}…";
        CounterText.Text = counter;
        UndoButton.IsEnabled = _undo.Count > 0;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(isError ? "Danger" : "TextMuted");
    }

    // ------------------------------------------------------------------ sorting

    private void SortTo(Sorter sorter)
    {
        ImageItem? item = Current;
        if (item is null)
        {
            SetStatus(_folder is null ? "Open a folder first." : "Nothing left to sort here.");
            return;
        }
        if (SamePath(Path.GetDirectoryName(item.Path), sorter.Folder))
        {
            SetStatus($"{item.Name} is already in {sorter.Name}.");
            return;
        }

        SortMode mode = Mode;

        // Capture what the picture looks like and where it is before the list changes underneath it.
        ImageSource? flying = item.IsVideo ? item.ThumbIfLoaded : MainImage.Source ?? item.ThumbIfLoaded;
        Rect from = VisualRectOf(item);
        ImageSource? thumb = item.ThumbIfLoaded
                             ?? (_shownItem == item && _shown is not null
                                 ? ImageDecoder.Downscale(_shown.Bitmap, ThumbService.Side)
                                 : null);
        if (item.IsVideo) StopVideo();   // lets go of the file so it can be moved

        var landing = new BinThumb(item.Name);
        sorter.Recent.Insert(0, landing);
        while (sorter.Recent.Count > MaxRecent) sorter.Recent.RemoveAt(sorter.Recent.Count - 1);
        sorter.Count++;

        var op = new SortOp { Item = item, Target = sorter, Thumb = landing, Mode = mode, Index = _index };
        _undo.Add(op);

        if (mode == SortMode.Move)
        {
            _previews.Remove(item.Path);
            int removedAt = _index;
            _items.RemoveAt(_index);
            RefreshFolderLabel(removedAt);   // the next picture may now be the first of its folder
            if (_index >= _items.Count) _index = _items.Count - 1;
        }
        else if (_index < _items.Count - 1)
        {
            _index++;
        }
        ShowCurrent();
        Fly(flying, from, sorter, landing, thumb);

        string source = item.Path, name = item.Name, folder = sorter.Folder;
        EnqueueIo(() =>
        {
            Directory.CreateDirectory(folder);
            string destination = UniquePath(folder, name);   // never overwrite something already there
            Retry(() =>
            {
                if (mode == SortMode.Move) File.Move(source, destination);
                else File.Copy(source, destination);
            });
            op.Dest = destination;
        }, error =>
        {
            // The file never made it: put everything back the way it was.
            if (_undo.Remove(op)) RollBack(op);
            SetStatus($"Couldn't {(mode == SortMode.Move ? "move" : "copy")} {name}. {error.Message}", isError: true);
        });

        SetStatus($"{(mode == SortMode.Move ? "Moved" : "Copied")} {name} to {sorter.Name}.");
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            SetStatus("Nothing to undo.");
            return;
        }
        SortOp op = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        RollBack(op);

        string name = op.Item.Name, original = op.Item.Path;
        EnqueueIo(() =>
        {
            if (op.Dest is null) return;   // the original move never happened
            if (op.Mode == SortMode.Move)
            {
                if (File.Exists(original)) throw new IOException("A file with that name is back in the original folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                Retry(() => File.Move(op.Dest, original));
            }
            else
            {
                // The copy goes to the Recycle Bin rather than being erased outright.
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(op.Dest,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }, error => SetStatus($"Couldn't undo {name}. {error.Message}", isError: true));

        SetStatus(op.Mode == SortMode.Move ? $"Put {name} back." : $"Took back the copy of {name}.");
    }

    /// <summary>Undo the on-screen effects of a sort (the file itself is handled separately).</summary>
    private void RollBack(SortOp op)
    {
        op.Target.Count = Math.Max(0, op.Target.Count - 1);
        op.Target.Recent.Remove(op.Thumb);

        if (op.Mode == SortMode.Move)
        {
            int at = Math.Clamp(op.Index, 0, _items.Count);
            _items.Insert(at, op.Item);
            RefreshFolderLabel(at);
            RefreshFolderLabel(at + 1);
            _index = at;
        }
        else
        {
            int at = IndexOfPath(op.Item.Path);
            if (at >= 0) _index = at;
        }
        ShowCurrent();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void EnqueueIo(Action work, Action<Exception> onError)
    {
        _ioPending++;
        UpdateChrome();
        _ioTail = _ioTail.ContinueWith(_ =>
        {
            Exception? error = null;
            try { work(); }
            catch (Exception ex) { error = ex; }
            Dispatcher.InvokeAsync(() =>
            {
                _ioPending--;
                UpdateChrome();
                if (error is not null) onError(error);
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static string UniquePath(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>A file that was just on screen can stay locked for a moment (videos especially).</summary>
    private static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < 12 && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(120);
            }
        }
    }

    // ------------------------------------------------------------------ the flying picture

    private void Fly(ImageSource? picture, Rect from, Sorter target, BinThumb landing, ImageSource? thumb)
    {
        var card = BinsList.ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement;
        if (picture is null || card is null || from.Width < 1 || from.Height < 1)
        {
            landing.Image = thumb;
            if (card is not null) Pulse(card);
            return;
        }

        card.BringIntoView();
        Root.UpdateLayout();
        FrameworkElement slotHost = FindChild<ItemsControl>(card, "ThumbHost") ?? card;
        Rect to = slotHost.TransformToAncestor(Root)
                          .TransformBounds(new Rect(0, 0, BinThumbSize, BinThumbSize));

        // Starts as the whole picture and ends as the same square crop the card shows.
        var flyer = new Border
        {
            Width = from.Width,
            Height = from.Height,
            CornerRadius = new CornerRadius(6),
            Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(flyer, from.X);
        Canvas.SetTop(flyer, from.Y);
        Overlay.Children.Add(flyer);

        var duration = new Duration(TimeSpan.FromMilliseconds(FlyMilliseconds));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Tween(double a, double b) => new(a, b, duration) { EasingFunction = ease };

        DoubleAnimation left = Tween(from.X, to.X);
        left.Completed += (_, _) =>
        {
            Overlay.Children.Remove(flyer);
            landing.Image = thumb;
            Pulse(card);
        };
        flyer.BeginAnimation(Canvas.LeftProperty, left);
        flyer.BeginAnimation(Canvas.TopProperty, Tween(from.Y, to.Y));
        flyer.BeginAnimation(WidthProperty, Tween(from.Width, to.Width));
        flyer.BeginAnimation(HeightProperty, Tween(from.Height, to.Height));
    }

    /// <summary>Flash the card's outline so the eye lands on the right sorter.</summary>
    private static void Pulse(FrameworkElement card)
    {
        if (FindChild<Border>(card, "Card") is not { } border) return;
        var brush = new SolidColorBrush(AccentColor);
        border.BorderBrush = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(AccentColor, StrokeColor, TimeSpan.FromMilliseconds(750)));
    }

    /// <summary>Where the current picture sits on screen, in window coordinates.</summary>
    private Rect VisualRectOf(ImageItem item)
    {
        Rect host = ViewerHost.TransformToAncestor(Root).TransformBounds(new Rect(ViewerHost.RenderSize));
        FrameworkElement element = item.IsVideo ? Player : MainImage;
        if (element.IsVisible && element.ActualWidth >= 1 && element.ActualHeight >= 1)
        {
            Rect rect = element.TransformToAncestor(Root).TransformBounds(new Rect(element.RenderSize));
            rect.Intersect(host);
            if (!rect.IsEmpty && rect.Width >= 1 && rect.Height >= 1) return rect;
        }
        double w = host.Width * 0.6, h = host.Height * 0.6;
        return new Rect(host.X + (host.Width - w) / 2, host.Y + (host.Height - h) / 2, w, h);
    }

    // ------------------------------------------------------------------ filmstrip

    private void SyncFilmstrip()
    {
        _syncingFilmstrip = true;
        Filmstrip.SelectedIndex = _index;
        _syncingFilmstrip = false;

        ScrollFilmstripToCurrent();
        if (_filmNeedsSettle)
        {
            // A new list has a new length that the strip only knows after its next layout pass.
            // Scroll once more after that, or the strip can be left showing the wrong stretch.
            _filmNeedsSettle = false;
            Dispatcher.BeginInvoke(new Action(ScrollFilmstripToCurrent), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Keep the current picture in the middle of the strip.</summary>
    private void ScrollFilmstripToCurrent()
    {
        if (_index < 0) return;
        _filmScroll ??= FindChild<ScrollViewer>(Filmstrip);
        if (_filmScroll is null) return;
        double offset = _index * FilmCell - (_filmScroll.ViewportWidth - FilmCell) / 2;
        _filmScroll.ScrollToHorizontalOffset(Math.Max(0, offset));
    }

    private void Filmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int selected = Filmstrip.SelectedIndex;
        if (_syncingFilmstrip || selected < 0 || selected == _index) return;
        _index = selected;
        ShowCurrent();
    }

    private void Filmstrip_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _filmScroll ??= FindChild<ScrollViewer>(Filmstrip);
        if (_filmScroll is null) return;
        _filmScroll.ScrollToHorizontalOffset(_filmScroll.HorizontalOffset - e.Delta * 1.5);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ zoom and pan

    private void Viewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Current is not { IsVideo: false } || MainImage.Source is null) return;
        ZoomAt(e.GetPosition(ViewerHost), ZoomScale.ScaleX * (e.Delta > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    private void Viewer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Current is not { } item) return;
        if (item.IsVideo)
        {
            if (Player.Source is not null) TogglePlay();
            return;
        }

        Point at = e.GetPosition(ViewerHost);
        if (e.ClickCount == 2)
        {
            // Double-click flips between "fit the window" and "actual pixels".
            ZoomAt(at, ZoomScale.ScaleX > 1.01 ? 1 : ActualSizeScale());
            return;
        }
        if (ZoomScale.ScaleX > 1.01)
        {
            _panning = true;
            _panFrom = at;
            _panOrigin = new Point(ZoomPan.X, ZoomPan.Y);
            ViewerHost.CaptureMouse();
        }
    }

    private void Viewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        Point at = e.GetPosition(ViewerHost);
        ZoomPan.X = _panOrigin.X + (at.X - _panFrom.X);
        ZoomPan.Y = _panOrigin.Y + (at.Y - _panFrom.Y);
        ClampPan();
    }

    private void Viewer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ViewerHost.ReleaseMouseCapture();
    }

    private void ZoomAt(Point point, double scale)
    {
        double old = ZoomScale.ScaleX;
        scale = Math.Clamp(scale, 1, Math.Max(8, ActualSizeScale() * 4));
        if (Math.Abs(scale - old) < 1e-6) return;

        // Keep whatever is under the cursor under the cursor.
        double ratio = scale / old;
        ZoomPan.X = point.X - (point.X - ZoomPan.X) * ratio;
        ZoomPan.Y = point.Y - (point.Y - ZoomPan.Y) * ratio;
        ZoomScale.ScaleX = ZoomScale.ScaleY = scale;
        ClampPan();

        ViewerHost.Cursor = scale > 1.01 ? Cursors.SizeAll : null;
        if (scale > 1.01) RequestFullResolution();
    }

    private void ClampPan()
    {
        double scale = ZoomScale.ScaleX;
        ZoomPan.X = Math.Clamp(ZoomPan.X, ViewerHost.ActualWidth * (1 - scale), 0);
        ZoomPan.Y = Math.Clamp(ZoomPan.Y, ViewerHost.ActualHeight * (1 - scale), 0);
    }

    private void ResetZoom()
    {
        ZoomScale.ScaleX = ZoomScale.ScaleY = 1;
        ZoomPan.X = ZoomPan.Y = 0;
        ViewerHost.Cursor = null;
        if (_panning)
        {
            _panning = false;
            ViewerHost.ReleaseMouseCapture();
        }
    }

    /// <summary>The zoom factor at which one picture pixel is one screen pixel.</summary>
    private double ActualSizeScale()
    {
        if (_shown is null || MainImage.ActualWidth < 1) return 2;
        double scale = _shown.FullWidth / (MainImage.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        return scale > 1.05 ? scale : 2;
    }

    /// <summary>Previews are decoded at screen size for speed; fetch every pixel once the user zooms in.</summary>
    private async void RequestFullResolution()
    {
        if (_fullRequested || _shown is not { IsFull: false } || _shownItem is not { } item) return;
        _fullRequested = true;
        int version = _showVersion;
        DecodedImage? full = await Task.Run(() =>
        {
            try { return ImageDecoder.Decode(item.Path, 0); }
            catch { return null; }
        });
        if (version != _showVersion || full is null) return;
        _shown = full;
        MainImage.Source = full.Bitmap;
    }

    // ------------------------------------------------------------------ video

    private void StartVideo(ImageItem item)
    {
        Player.Visibility = Visibility.Visible;
        VideoBar.Visibility = Visibility.Visible;
        Player.IsMuted = _settings.Muted;
        MuteGlyph.Text = _settings.Muted ? "" : "";
        _syncingSlider = true;
        SeekSlider.Value = 0;
        _syncingSlider = false;
        TimeText.Text = "";
        try
        {
            Player.Source = new Uri(item.Path);
            Player.Play();
            SetPlaying(true);
            _videoTimer.Start();
        }
        catch
        {
            ShowVideoProblem();
        }
    }

    private void StopVideo()
    {
        _videoTimer.Stop();
        if (Player.Source is not null)
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
        }
        Player.Visibility = Visibility.Collapsed;
        VideoBar.Visibility = Visibility.Collapsed;
        _playing = false;
    }

    private void ShowVideoProblem()
    {
        ImageItem? item = Current;
        StopVideo();
        SetStill(item?.ThumbIfLoaded);
        ShowProblem("Windows can't play this video here, but you can still sort it.");
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayGlyph.Text = playing ? "" : "";
    }

    private void TogglePlay()
    {
        if (_playing) Player.Pause();
        else Player.Play();
        SetPlaying(!_playing);
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        _syncingSlider = true;
        SeekSlider.Maximum = Math.Max(0.1, Player.NaturalDuration.TimeSpan.TotalSeconds);
        _syncingSlider = false;
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;   // loop, so there is always something to look at
        Player.Play();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e) => ShowVideoProblem();

    private void VideoTimer_Tick(object? sender, EventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        TimeSpan position = Player.Position, total = Player.NaturalDuration.TimeSpan;
        _syncingSlider = true;
        SeekSlider.Value = position.TotalSeconds;
        _syncingSlider = false;
        TimeText.Text = $"{Clock(position)} / {Clock(total)}";
    }

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || Player.Source is null) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is not null) TogglePlay();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _settings.Muted = !_settings.Muted;
        Player.IsMuted = _settings.Muted;
        MuteGlyph.Text = _settings.Muted ? "" : "";
    }

    private void Swallow_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ------------------------------------------------------------------ sorters

    private void AddSorter_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Pick the folder to sort into" };
        if (_folder is not null && Directory.Exists(_folder)) picker.InitialDirectory = _folder;
        if (picker.ShowDialog(this) != true) return;

        string folder = picker.FolderName;
        var dialog = new SorterDialog(this, "New sorter", new DirectoryInfo(folder).Name, folder, NextFreeKey(), _sorters);
        if (dialog.ShowDialog() != true) return;

        TakeKey(dialog.Hotkey, except: null);
        _sorters.Add(new Sorter(dialog.SorterName, dialog.Folder, dialog.Hotkey));
        SortersChanged(foldersChanged: true);
    }

    private void BinEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SorterOf(sender) is not { } sorter) return;
        var dialog = new SorterDialog(this, "Edit sorter", sorter.Name, sorter.Folder, sorter.Key,
            _sorters.Where(s => s != sorter));
        if (dialog.ShowDialog() != true) return;

        TakeKey(dialog.Hotkey, except: sorter);
        sorter.Name = dialog.SorterName;
        sorter.Folder = dialog.Folder;
        sorter.Key = dialog.Hotkey;
        SortersChanged(foldersChanged: true);
    }

    private void BinOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SorterOf(sender) is not { } sorter) return;
        try
        {
            Directory.CreateDirectory(sorter.Folder);
            Process.Start(new ProcessStartInfo { FileName = sorter.Folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open that folder. {ex.Message}", isError: true);
        }
    }

    private void BinUp_Click(object sender, RoutedEventArgs e) => MoveSorter(sender, -1);

    private void BinDown_Click(object sender, RoutedEventArgs e) => MoveSorter(sender, +1);

    private void MoveSorter(object sender, int delta)
    {
        if (SorterOf(sender) is not { } sorter) return;
        int from = _sorters.IndexOf(sorter), to = from + delta;
        if (from < 0 || to < 0 || to >= _sorters.Count) return;
        _sorters.Move(from, to);
        SortersChanged(foldersChanged: false);
    }

    private void BinRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SorterOf(sender) is not { } sorter) return;
        _sorters.Remove(sorter);   // only the button goes; the folder and its files are untouched
        SetStatus($"Removed {sorter.Name} from the list. Its folder was left alone.");
        SortersChanged(foldersChanged: true);
    }

    private void Bin_Click(object sender, MouseButtonEventArgs e)
    {
        if (SorterOf(sender) is { } sorter) SortTo(sorter);
    }

    private void BinMenu_Click(object sender, RoutedEventArgs e)
    {
        DependencyObject? node = sender as DependencyObject;
        while (node is not null and not Border { Name: "Card" }) node = VisualTreeHelper.GetParent(node);
        if (node is not Border { ContextMenu: { } menu } card) return;
        menu.PlacementTarget = card;
        menu.Placement = PlacementMode.MousePoint;
        menu.DataContext = card.DataContext;
        menu.IsOpen = true;
    }

    private static Sorter? SorterOf(object sender) => (sender as FrameworkElement)?.DataContext as Sorter;

    private Key? NextFreeKey()
    {
        Key[] order = [Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0];
        foreach (Key key in order)
            if (_sorters.All(s => s.Key != key)) return key;
        return null;
    }

    /// <summary>A hotkey belongs to one sorter at a time.</summary>
    private void TakeKey(Key? key, Sorter? except)
    {
        if (key is null) return;
        foreach (Sorter other in _sorters)
            if (other != except && other.Key == key) other.Key = null;
    }

    private async void SortersChanged(bool foldersChanged)
    {
        BinsEmpty.Visibility = _sorters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveSettings();
        // With subfolders on, files inside a sorter's own folder must drop out of (or come back into) the list.
        if (foldersChanged && _folder is not null && SubfoldersCheck.IsChecked == true)
            await LoadFolder(_folder, keepPosition: true);
    }

    // ------------------------------------------------------------------ helpers

    private int IndexOfPath(string path)
    {
        for (int i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Normal(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>True when <paramref name="path"/> is somewhere below <paramref name="root"/> (not the same folder).</summary>
    private static bool IsInside(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        try
        {
            return Normal(path).StartsWith(Normal(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static T? FindChild<T>(DependencyObject root, string? name = null) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (name is null || match.Name == name)) return match;
            if (FindChild<T>(child, name) is { } deeper) return deeper;
        }
        return null;
    }
}

/// <summary>One sort the user did, with enough detail to take it back.</summary>
internal sealed class SortOp
{
    public required ImageItem Item { get; init; }
    public required Sorter Target { get; init; }
    public required BinThumb Thumb { get; init; }
    public SortMode Mode { get; init; }
    public int Index { get; init; }

    /// <summary>Where the file ended up. Set by the file worker; null until (or unless) the move succeeds.</summary>
    public volatile string? Dest;
}

/// <summary>Keeps the last few decoded previews so stepping back and forth is instant. UI thread only.</summary>
internal sealed class PreviewCache
{
    private const int Capacity = 7;
    private readonly Dictionary<string, (Task<DecodedImage?> Task, int Side)> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();

    public Task<DecodedImage?> Get(string path, int side)
    {
        bool known = _tasks.TryGetValue(path, out var existing);
        _recent.Remove(path);
        _recent.AddFirst(path);
        // A preview decoded for a smaller window isn't good enough once the window has grown.
        if (known && existing.Side >= side) return existing.Task;

        Task<DecodedImage?> task = Task.Run(() =>
        {
            try { return ImageDecoder.Decode(path, side); }
            catch { return null; }
        });
        _tasks[path] = (task, side);
        while (_recent.Count > Capacity)
        {
            _tasks.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }
        return task;
    }

    public void Remove(string path)
    {
        if (_tasks.Remove(path)) _recent.Remove(path);
    }

    public void Clear()
    {
        _tasks.Clear();
        _recent.Clear();
    }
}
