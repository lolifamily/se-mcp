using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Sandbox;
using VRage.FileSystem;
using VRageMath;
using VRageRender;

namespace ClientPlugin;

// Screenshot capture as a single-slot service, deliberately OUTSIDE the Executor:
// the waiter is an async HTTP task, not the frame loop. Only two things need the
// main thread — MyRenderProxy.TakeScreenshot (the render message queue is
// single-producer from the update thread) and reading ScreenSize — so Tick()
// does just the issuing. Completion arrives via PatchScreenshotTaken on the
// render thread (stock) or a background task (HdrRender-style savers) and
// fulfills WorkItem.Done directly; RunContinuationsAsynchronously puts the
// response work (file read + base64) on the thread pool, off game threads.
internal static class ScreenshotService
{
    // Single area cap, ~3.75MP. Bounds the encoded size (worst-case 8K JPEG
    // base64 stays clear of the API's 10MB per-image hard limit); the value
    // mirrors the current-generation vision input budget (4784 visual tokens
    // x 28^2 px each). Server-side rules (long-edge etc.) are deliberately
    // NOT replicated here — the API crops/scales the remainder itself.
    private const double MaxPixels = 3_750_656;

    // Wall clock, not frames: encoding may run on a background task at its own
    // pace. Covers the one unsignalled failure mode — a same-frame F4 overwrites
    // MyRender11.m_screenshot (a single slot), so our message is consumed but no
    // ScreenshotTaken ever fires for our path.
    private const int TimeoutMs = 8000;

    private const string ShutdownMessage = "[server shutting down]";

    private sealed class Request
    {
        public WorkItem Item;
        public bool IgnoreSprites;
        public string Path;       // null until the main thread issues TakeScreenshot
        public Stopwatch Started;
    }

    // Single slot: in-flight gate and claim token in one. Writes go through
    // Interlocked; reads through Volatile.Read. Whoever wins the CompareExchange
    // back to null (signal, timeout, cancel, drain) owns completing the item —
    // the losers see a different/cleared slot and do nothing.
    private static Request _pending;

    // Flipped by Drain (Plugin.Dispose, main thread) before the listener stops.
    // After that point Plugin.Update never runs again: a request that slipped
    // into the slot would never be issued, never time out, and leave its
    // HandleToolsCall awaiting forever. Same contract as Executor.disposed.
    private static volatile bool _disposed;

    // HTTP thread. Slot taken => reject immediately; MyRender11.m_screenshot is a
    // single slot too, so concurrent captures would silently overwrite each other.
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

        // Double-check after taking the slot. If Drain ran in between the first
        // check and the CompareExchange, its claim missed this request; self-correct
        // here so no WorkItem hangs past Dispose (mirrors Executor.Enqueue).
        if (_disposed && TryClaim(req))
            Complete(item, error: ShutdownMessage);
    }

    // Main thread, called from Plugin.Update every frame. Polices cancellation
    // first — a request is cancellable in any state — then advances the state
    // machine: not yet issued → issue; issued → wait for the signal or deadline.
    public static void Tick()
    {
        var req = Volatile.Read(ref _pending);
        if (req == null)
            return;

        if (req.Item.Cancel.IsCancellationRequested)
        {
            // Before issue nothing was sent and nothing lands on disk. After issue
            // the capture message is already in flight and the file still lands —
            // it just joins the kept history like every other shot.
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
                var path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jpg");

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
        if (TryClaim(req))
            Complete(req.Item, error: $"screenshot not reported within {TimeoutMs}ms (expected at {req.Path}); " +
                                      $"a concurrent screenshot (e.g. F4) may have displaced it, retry");
    }

    // Patch thread (render or background). Every screenshot in the game funnels
    // through MyRenderProxy.ScreenshotTaken; claim only our own path — F4 and
    // blueprint shots carry different filenames and fall through. A late signal
    // for a timed-out/cancelled request finds the slot cleared and is ignored.
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

    // Plugin.Dispose, before McpServer.Dispose — close the door, then fulfill the
    // pending promise so the awaiting HandleToolsCall wakes up and writes its
    // response while the listener is still alive (same ordering contract as the
    // Executors). A Begin racing past the flag is caught by its double-check.
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
