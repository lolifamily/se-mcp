using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Sandbox;
using Shared.Mcp;
using VRage.FileSystem;
using VRageMath;
using VRageRender;

namespace ClientPlugin;

// take_screenshot, as a single-slot service rather than a script: what waits is the HTTP request,
// not a frame. Only issuing needs the main thread (Tick): MyRenderProxy.TakeScreenshot, whose
// message queue has the update thread as its one producer, and ScreenSize. Completion comes through
// PatchScreenshotTaken, on the render thread or a saver's background task (HdrRender and the like),
// and answers the request from there.
internal static class ScreenshotService
{
    // ~3.75 MP: the current generation's vision input budget (4784 visual tokens of 28x28 px), and it
    // keeps a worst-case 8K JPEG's base64 under the API's hard 10 MB per image. The API's other rules
    // (long edge and so on) aren't repeated here: it scales what remains itself.
    private const double MaxPixels = 3_750_656;

    // Wall clock, not frames: a background saver encodes at its own pace. Covers the one failure
    // nothing reports: an F4 in the same frame overwrites MyRender11.m_screenshot, a single slot, so
    // our message is taken but no ScreenshotTaken ever comes for our path.
    private const int TimeoutMs = 8000;

    private const string ShutdownMessage = "[server shutting down]";

    private sealed class Request
    {
        public WorkItem Item;
        public bool IgnoreSprites;
        public string Path;       // null until the main thread issues TakeScreenshot
        public Stopwatch Started;
    }

    // The slot: the request in flight, and the claim on it. Whoever swaps it back to null (signal,
    // timeout, cancel, drain) answers the request; the others find it gone and do nothing.
    private static Request _pending;

    // Set by Drain, after which no main-lane pump comes: a request taken then would never be issued
    // or time out, and its HTTP request would wait for good.
    private static volatile bool _disposed;

    // HTTP thread. A taken slot refuses at once: MyRender11.m_screenshot is a single slot too, and
    // two captures would overwrite each other unnoticed.
    public static void Begin(WorkItem item, bool ignoreSprites)
    {
        if (_disposed)
        {
            Complete(item, error: ShutdownMessage);
            return;
        }

        var req = new Request { Item = item, IgnoreSprites = ignoreSprites };
        if (Interlocked.CompareExchange(ref _pending, req, null) != null)
        {
            Complete(item, error: "another screenshot is already in flight, retry shortly");
            return;
        }

        // Drain may have run between the first check and taking the slot.
        if (_disposed && TryClaim(req))
            Complete(item, error: ShutdownMessage);
    }

    // Main thread, every frame (Plugin.Pump). A cancel ends the request in any state; otherwise it is
    // issued, or, once issued, waits for its signal or deadline.
    public static void Tick()
    {
        var req = Volatile.Read(ref _pending);
        if (req == null)
            return;

        if (req.Item.Cancel.IsCancellationRequested)
        {
            // Before issue nothing was sent and nothing lands on disk. After it the file still lands,
            // and joins the kept history like every other shot.
            if (TryClaim(req))
                Complete(req.Item, cancelled: true);
            return;
        }

        if (req.Path == null)
        {
            try
            {
                var dir = Path.Combine(MyFileSystem.UserDataPath, "Screenshots", "SeMcp");
                Directory.CreateDirectory(dir);
                // InvariantCulture: under fa-IR, ar-SA and the like the format would write native
                // digits into the name.
                var path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".jpg");

                var screen = MySandboxGame.ScreenSize;
                var pixels = (double)screen.X * screen.Y;
                var scale = pixels > MaxPixels ? (float)Math.Sqrt(MaxPixels / pixels) : 1f;

                // Path must be published before the message is enqueued: the render
                // thread observes the message through the queue's own synchronization,
                // which carries this write along, so NotifyScreenshotTaken can match.
                req.Started = Stopwatch.StartNew();
                req.Path = path;
                MyRenderProxy.TakeScreenshot(new Vector2(scale), path,
                    debug: false, ignoreSprites: req.IgnoreSprites, showNotification: false);
            }
            catch (Exception ex)
            {
                if (TryClaim(req))
                    Complete(req.Item, error: $"failed to issue screenshot: {ex.Message}");
            }
            return;
        }

        if (req.Started.ElapsedMilliseconds <= TimeoutMs) return;
        // The likely cause stays here, not in the report: a same-frame F4 displaced ours (see
        // TimeoutMs), and the file may still land at req.Path later. The report says what to do.
        if (TryClaim(req))
            Complete(req.Item, error: $"screenshot not reported within {TimeoutMs}ms — retry");
    }

    // Render thread or a saver's. Every screenshot in the game comes through here; only our path is
    // claimed, so F4 and blueprint shots fall through, and a late signal for a request that timed out
    // or was cancelled finds the slot empty.
    public static void NotifyScreenshotTaken(bool success, string filename)
    {
        var req = Volatile.Read(ref _pending);
        if (req == null || filename == null)
            return;
        var path = req.Path;
        if (path == null || !string.Equals(filename, path, StringComparison.OrdinalIgnoreCase))
            return;
        if (!TryClaim(req))
            return;

        if (success)
        {
            req.Item.Output = path;
            Complete(req.Item);
        }
        else
        {
            Complete(req.Item, error: "game reported screenshot save failure (see game log)");
        }
    }

    // Plugin.Dispose, before the listener stops: no new requests, and the one in flight answered
    // while its response can still be written. A Begin that slips past the flag sees it afterwards.
    public static void Drain()
    {
        _disposed = true;
        var req = Volatile.Read(ref _pending);
        if (req != null && TryClaim(req))
            Complete(req.Item, error: ShutdownMessage);
    }

    private static bool TryClaim(Request req)
    {
        return Interlocked.CompareExchange(ref _pending, null, req) == req;
    }

    private static void Complete(WorkItem item, string error = null, bool cancelled = false)
    {
        item.Error = error;
        item.WasCancelled = cancelled;
        item.Done.TrySetResult(true);
    }
}
