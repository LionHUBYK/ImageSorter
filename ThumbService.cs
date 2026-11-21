using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageSorter;

/// <summary>
/// Loads thumbnails on a few background threads. Newest requests are served first, so whatever
/// just scrolled into view wins over things that scrolled past.
/// </summary>
internal static class ThumbService
{
    public const int Side = 200;        // longer edge, in pixels
    private const int MaxLoaded = 700;  // thumbnails kept in memory before the oldest are dropped
    private const int MaxPending = 300; // far more than fit on screen at once
    private const int Workers = 3;

    private static readonly object Gate = new();
    private static readonly LinkedList<ImageItem> Pending = new();
    private static readonly LinkedList<ImageItem> Loaded = new();   // touched on the UI thread only
    private static Dispatcher? _ui;

    public static void Init(Dispatcher ui)
    {
        if (_ui is not null) return;
        _ui = ui;
        for (int i = 0; i < Workers; i++)
        {
            var thread = new Thread(Work) { IsBackground = true, Name = "Thumbnails" };
            thread.SetApartmentState(ApartmentState.STA);   // Explorer's thumbnail providers expect this
            thread.Start();
        }
    }

    /// <summary>Called on the UI thread the first time an item's thumbnail is read.</summary>
    public static void Request(ImageItem item)
    {
        ImageItem? dropped = null;
        lock (Gate)
        {
            Pending.AddFirst(item);
            if (Pending.Count > MaxPending)
            {
                dropped = Pending.Last!.Value;
                Pending.RemoveLast();
            }
            Monitor.Pulse(Gate);
        }
        // Tell the dropped item later, not from inside this call: it re-requests if its cell is still showing.
        if (dropped is not null) _ui?.BeginInvoke(new Action(dropped.CancelRequest), DispatcherPriority.Background);
    }

    private static void Work()
    {
        while (true)
        {
            ImageItem item;
            lock (Gate)
            {
                while (Pending.Count == 0) Monitor.Wait(Gate);
                item = Pending.First!.Value;
                Pending.RemoveFirst();
            }

            // A virus scanner or a file still being copied can block the first read, which would
            // otherwise leave that cell blank for good. Try a few times before giving up.
            BitmapSource? bitmap = null;
            for (int attempt = 0; attempt < 3 && bitmap is null; attempt++)
            {
                if (attempt > 0)
                {
                    if (!File.Exists(item.Path)) break;   // sorted away in the meantime
                    Thread.Sleep(250);
                }
                try { bitmap = ImageDecoder.Thumbnail(item.Path, item.Kind, Side); }
                catch { /* try again, or show an empty cell */ }
            }

            _ui?.BeginInvoke(new Action(() => Deliver(item, bitmap)), DispatcherPriority.Background);
        }
    }

    private static void Deliver(ImageItem item, BitmapSource? bitmap)
    {
        item.SetThumb(bitmap);
        if (bitmap is null) return;

        Loaded.AddFirst(item);
        while (Loaded.Count > MaxLoaded)
        {
            ImageItem oldest = Loaded.Last!.Value;
            Loaded.RemoveLast();
            oldest.DropThumb();
        }
    }
}
