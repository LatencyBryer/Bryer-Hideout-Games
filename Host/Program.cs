/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.OffScreen;
using CefSharp.Handler;

namespace BryersHideoutGamesHost;

/// <summary>
/// Independent Chromium process. No Dalamud references, no chat/FFXIV interaction.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0].Length > 80 || !args[0].StartsWith("bryer-", StringComparison.Ordinal) ||
            args[0].Any(c => !char.IsLetterOrDigit(c) && c != '-') || !int.TryParse(args[1], out var parentId) || parentId < 1)
            return 2;

        using var host = new BrowserHost(args[0], parentId, args[2]);
        try { return host.Run(); }
        catch (Exception ex)
        {
            // Never write exception messages or URLs here (access code might be in a URL).
            host.LogDiagnostic("Fatal browser helper failure: " + ex.GetType().Name);
            return 3;
        }
    }
}

internal sealed class BrowserHost : IDisposable
{
    private readonly string pipeId;
    private readonly int parentId;
    private readonly string profile;
    private readonly object browserLock = new();
    // Paint buffers must not contend with CEF input/resize commands.
    private readonly object paintLock = new();
    private readonly CancellationTokenSource stop = new();
    private ChromiumWebBrowser? browser;
    private Uri? allowedGameUri;
    private bool cefInitialized;
    private int width = 720, height = 490;
    // Honor the configured FPS before and after clicking the browser surface.
    private int browserFrameRate = 30;
    private int browserFocused;
    private CefEventFlags pressedMouseFlags = CefEventFlags.None;
    private byte[]? latestPaint;
    private int latestPaintWidth;
    private int latestPaintHeight;
    private int dirty = 1;
    // The Dalamud side explicitly acknowledges a frame after the GPU upload.
    // Never queue more than one large bitmap across IPC. This is essential for
    // stable frame pacing in FFXIV while resizing or during heavy site animation.
    private int frameCredit = 1;
    private long lastPaintTicks = Stopwatch.GetTimestamp();
    private long lastInvalidateTicks;
    private int skippedPaints;
    private long lastTelemetryTicks = Stopwatch.GetTimestamp();
    private int paintCount;
    private int sentFrames;
    private int ackCount;
    private string pendingStatus = "Chromium starting...";
    private string? lastStatus;

    public BrowserHost(string pipeId, int parentId, string profile)
    {
        this.pipeId = pipeId;
        this.parentId = parentId;
        this.profile = Path.GetFullPath(profile);
    }

    public int Run()
    {
        Directory.CreateDirectory(profile);
        using var commandPipe = new NamedPipeServerStream(pipeId + "-commands", PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var framePipe = new NamedPipeServerStream(pipeId + "-frames", PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task.WaitAll(commandPipe.WaitForConnectionAsync(), framePipe.WaitForConnectionAsync());
        if (!ParentAlive()) return 4;

        var baseDirectory = AppContext.BaseDirectory;
        var subprocess = Path.Combine(baseDirectory, "CefSharp.BrowserSubprocess.exe");
        if (!File.Exists(subprocess)) throw new FileNotFoundException("CEF browser subprocess missing.");
        var cef = new CefSettings
        {
            CachePath = profile,
            RootCachePath = profile,
            BrowserSubprocessPath = subprocess,
            LogFile = Path.Combine(profile, "cef-diagnostics.log"),
            LogSeverity = LogSeverity.Warning,
        };
        cef.EnableAudio();
        var locales = Path.Combine(baseDirectory, "locales");
        if (Directory.Exists(locales)) cef.LocalesDirPath = locales;
        if (File.Exists(Path.Combine(baseDirectory, "cef.pak"))) cef.ResourcesDirPath = baseDirectory;
        if (!Cef.Initialize(cef, performDependencyCheck: true, browserProcessHandler: null))
            throw new InvalidOperationException("CEF initialization returned false.");
        cefInitialized = true;
        pendingStatus = "Browser ready";
        LogDiagnostic("Chromium host v16 initialized (IPC v13, SPA navigation guard).");

        // Main thread remains responsible for Chromium initialization/shutdown.
        var frames = Task.Run(() => PublishFramesAsync(framePipe, stop.Token));
        var commands = Task.Run(() => ReceiveCommandsAsync(commandPipe, stop.Token));
        var watchdog = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!ParentAlive()) { stop.Cancel(); break; }
                await Task.Delay(1500);
            }
        });
        try
        {
            var completed = Task.WaitAny(frames, commands, watchdog);
            if (!stop.IsCancellationRequested)
            {
                var task = completed switch { 0 => frames, 1 => commands, _ => watchdog };
                // These messages deliberately contain no URLs or access codes.
                LogDiagnostic($"Browser worker stopped: index={completed}, state={task.Status}, fault={task.Exception?.GetBaseException().GetType().Name ?? "none"}");
            }
        }
        finally
        {
            stop.Cancel();
            try { commandPipe.Dispose(); framePipe.Dispose(); } catch { }
            try { Task.WaitAll([frames, commands], TimeSpan.FromSeconds(2)); } catch { }
            lock (browserLock) { browser?.Dispose(); browser = null; }
            Cef.Shutdown();
            cefInitialized = false;
        }
        return 0;
    }

    private bool ParentAlive()
    {
        try { using var parent = Process.GetProcessById(parentId); return !parent.HasExited; }
        catch { return false; }
    }

    public void LogDiagnostic(string message)
    {
        try
        {
            var path = Path.Combine(profile, "browser-helper.log");
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        catch { }
    }

    private async Task ReceiveCommandsAsync(Stream input, CancellationToken token)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null)
            {
                LogDiagnostic("Command pipe ended; browser host shutting down.");
                stop.Cancel();
                break;
            }
            if (line.Length > 4096) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var type = GetString(root, "type");
                if (type == "exit") { stop.Cancel(); break; }
                if (type == "frame-ack")
                {
                    Interlocked.Exchange(ref frameCredit, 1);
                    Interlocked.Increment(ref ackCount);
                    // Frames skipped during GPU upload have made the last
                    // delivered image obsolete. Ask CEF for the latest pixels.
                    if (Interlocked.Exchange(ref skippedPaints, 0) != 0)
                    {
                        lock (browserLock)
                        {
                            if (browser?.IsBrowserInitialized == true)
                                browser.GetBrowserHost()?.Invalidate(PaintElementType.View);
                        }
                    }
                    continue;
                }
                if (type == "open")
                {
                    var url = GetString(root, "url");
                    if (GameNavigationGuard.Allowed(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        lock (browserLock)
                        {
                            // A new game gets a new navigation guard; never carry the old host allowlist.
                            if (browser != null && allowedGameUri != null &&
                                !allowedGameUri.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
                            {
                                browser.Dispose();
                                browser = null;
                                lock (paintLock)
                                {
                                    latestPaint = null;
                                    latestPaintWidth = latestPaintHeight = 0;
                                }
                                Interlocked.Exchange(ref frameCredit, 1);
                            }
                            if (browser == null)
                            {
                                browserFrameRate = Math.Clamp(GetInt(root, "fps"), 30, 60);
                                var settings = new BrowserSettings { WindowlessFrameRate = browserFrameRate };
                                browser = new ChromiumWebBrowser(uri.AbsoluteUri, settings)
                                {
                                    RequestHandler = new GameNavigationGuard(uri),
                                    LifeSpanHandler = new GamePopupGuard(),
                                };
                                allowedGameUri = uri;
                                Volatile.Write(ref lastPaintTicks, Stopwatch.GetTimestamp());
                                Interlocked.Exchange(ref dirty, 1);
                                // Recover any disallowed main-frame navigation without opening a homepage.
                                var guardedBrowser = browser;
                                browser.AddressChanged += (_, e) =>
                                {
                                    if (ReferenceEquals(browser, guardedBrowser) &&
                                        !GameNavigationGuard.Allowed(e.Address, uri))
                                    {
                                        LogDiagnostic("Blocked navigation outside the selected minigame.");
                                        _ = Task.Run(() =>
                                        {
                                            try { if (!stop.IsCancellationRequested && ReferenceEquals(browser, guardedBrowser)) guardedBrowser.Load(uri.AbsoluteUri); }
                                            catch (Exception ex) { LogDiagnostic("Navigation recovery failed: " + ex.GetType().Name); }
                                        });
                                    }
                                };
                                browser.Size = new Size(width, height);
                                browser.Paint += (_, paint) =>
                                {
                                    // CEF provides BGRA directly. Copy while BufferHandle is valid;
                                    // avoid Bitmap/LockBits/ScreenshotOrNull on every frame.
                                    if (paint.IsPopup || paint.BufferHandle == IntPtr.Zero ||
                                        paint.Width < 1 || paint.Height < 1 ||
                                        paint.Width > 4096 || paint.Height > 3072) return;
                                    // Full BGRA copies are expensive. While a frame is in
                                    // flight (pipe transfer + GPU upload), discard CEF paints
                                    // instead of allocating/copying a new bitmap for each one.
                                    // The next ACK requests a fresh paint, not a stale queue.
                                    if (Volatile.Read(ref frameCredit) == 0)
                                    {
                                        Interlocked.Exchange(ref skippedPaints, 1);
                                        return;
                                    }
                                    var bytes = new byte[checked(paint.Width * paint.Height * 4)];
                                    Marshal.Copy(paint.BufferHandle, bytes, 0, bytes.Length);
                                    lock (paintLock)
                                    {
                                        // Credit may have been consumed during Marshal.Copy.
                                        if (Volatile.Read(ref frameCredit) == 0)
                                        {
                                            Interlocked.Exchange(ref skippedPaints, 1);
                                            return;
                                        }
                                        latestPaint = bytes;
                                        latestPaintWidth = paint.Width;
                                        latestPaintHeight = paint.Height;
                                        Volatile.Write(ref lastPaintTicks, Stopwatch.GetTimestamp());
                                        Interlocked.Increment(ref paintCount);
                                        Interlocked.Exchange(ref dirty, 1);
                                    }
                                };
                                browser.FrameLoadEnd += (_, e) =>
                                {
                                    // Some games use their own same-origin document inside an
                                    // iframe, so apply the exit guard in that frame as well.
                                    if (e.HttpStatusCode < 400)
                                    {
                                        try { e.Frame.ExecuteJavaScriptAsync(GameNavigationGuard.FrontendNavigationLock); }
                                        catch (Exception ex) { LogDiagnostic("Game navigation script failed: " + ex.GetType().Name); }
                                    }
                                    if (e.Frame.IsMain)
                                    {
                                        pendingStatus = e.HttpStatusCode >= 400 ? $"HTTP {e.HttpStatusCode}" : "Ready";
                                        Interlocked.Exchange(ref dirty, 1);
                                        LogDiagnostic("Main document finished loading (HTTP " + e.HttpStatusCode + ").");
                                    }
                                };
                                browser.LoadError += (_, e) =>
                                {
                                    if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted)
                                        pendingStatus = $"Page error ({e.ErrorCode})";
                                };
                            }
                            else browser.Load(uri.AbsoluteUri);
                        }
                        pendingStatus = "Loading website...";
                    }
                    continue;
                }
                lock (browserLock)
                {
                    if (type == "resize")
                    {
                        var w = GetInt(root, "width"); var h = GetInt(root, "height");
                        if (w >= 1 && h >= 1 && w <= 4096 && h <= 3072)
                        {
                            width = w; height = h;
                            if (browser != null)
                            {
                                browser.Size = new Size(width, height);
                            }
                            Interlocked.Exchange(ref dirty, 1);
                        }
                        continue;
                    }
                    if (browser?.IsBrowserInitialized != true) continue;
                    var control = browser.GetBrowserHost();
                    if (control == null) continue;
                    var x = Math.Clamp(GetInt(root, "x"), 0, width - 1);
                    var y = Math.Clamp(GetInt(root, "y"), 0, height - 1);
                    // Chromium needs LeftMouseButton on MOVE events while dragging
                    // a web slider; bare MouseEvent(None) prevents continuous drag.
                    var evt = new MouseEvent(x, y, pressedMouseFlags);
                    switch (type)
                    {
                        case "move": control.SendMouseMoveEvent(evt, GetBool(root, "outside")); break;
                        case "focus":
                            var focus = GetBool(root, "value");
                            Volatile.Write(ref browserFocused, focus ? 1 : 0);
                            if (!focus) pressedMouseFlags = CefEventFlags.None;
                            control.SendFocusEvent(focus);
                            break;
                        case "wheel": control.SendMouseWheelEvent(evt, GetInt(root, "horizontal"), GetInt(root, "vertical")); break;
                        case "click":
                            var button = GetString(root, "button") switch
                            {
                                "right" => MouseButtonType.Right,
                                "middle" => MouseButtonType.Middle,
                                _ => MouseButtonType.Left,
                            };
                            var buttonFlag = button switch
                            {
                                MouseButtonType.Right => CefEventFlags.RightMouseButton,
                                MouseButtonType.Middle => CefEventFlags.MiddleMouseButton,
                                _ => CefEventFlags.LeftMouseButton,
                            };
                            if (GetBool(root, "down")) pressedMouseFlags |= buttonFlag;
                            else pressedMouseFlags &= ~buttonFlag;
                            control.SendMouseClickEvent(new MouseEvent(x, y, pressedMouseFlags), button, !GetBool(root, "down"), 1);
                            break;
                        case "key":
                            control.SendKeyEvent(new KeyEvent
                            {
                                Type = GetBool(root, "down") ? KeyEventType.RawKeyDown : KeyEventType.KeyUp,
                                WindowsKeyCode = GetInt(root, "vk"),
                                NativeKeyCode = GetInt(root, "scan"),
                                Modifiers = Flags(GetInt(root, "modifiers")),
                            });
                            break;
                        case "char":
                            control.SendKeyEvent(new KeyEvent
                            {
                                Type = KeyEventType.Char,
                                WindowsKeyCode = GetInt(root, "code"),
                                NativeKeyCode = GetInt(root, "scan"),
                                Modifiers = Flags(GetInt(root, "modifiers")),
                            });
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                pendingStatus = "Input event failed (" + ex.GetType().Name + ")";
            }
        }
    }

    private static CefEventFlags Flags(int mods) =>
        ((mods & 1) != 0 ? CefEventFlags.ControlDown : CefEventFlags.None) |
        ((mods & 2) != 0 ? CefEventFlags.ShiftDown : CefEventFlags.None) |
        ((mods & 4) != 0 ? CefEventFlags.AltDown : CefEventFlags.None);

    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var x) && x.TryGetInt32(out var n) ? n : 0;
    private static bool GetBool(JsonElement root, string name) => root.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.True;
    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";

    private async Task PublishFramesAsync(Stream output, CancellationToken token)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        // Handshake before any image/status bytes. A stale v9/v10 helper must not
        // silently send a different frame format to a newly installed plugin DLL.
        writer.Write((byte)4);
        writer.Write(unchecked((int)0x42525952)); // BRYR, shared protocol magic
        writer.Write(13); // IPC protocol version
        writer.Flush();
        LogDiagnostic("IPC protocol v13 handshake sent.");
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (lastStatus != pendingStatus)
                {
                    var message = pendingStatus;
                    var bytes = Encoding.UTF8.GetBytes(message);
                    writer.Write((byte)2);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                    lastStatus = message;
                }
                // CEF can stop emitting Paint notifications after page transitions or
                // resizes while JavaScript/audio still run. Request a bounded repaint
                // instead of letting the ImGui texture freeze at the last screenshot.
                // Do not request a repaint while a previous frame is awaiting upload.
                var nowTicks = Stopwatch.GetTimestamp();
                if (Volatile.Read(ref frameCredit) != 0 &&
                    Stopwatch.GetElapsedTime(Volatile.Read(ref lastPaintTicks), nowTicks).TotalMilliseconds > 420 &&
                    (lastInvalidateTicks == 0 || Stopwatch.GetElapsedTime(lastInvalidateTicks, nowTicks).TotalMilliseconds > 550))
                {
                    lastInvalidateTicks = nowTicks;
                    lock (browserLock)
                    {
                        if (browser?.IsBrowserInitialized == true)
                        {
                            browser.GetBrowserHost()?.Invalidate(PaintElementType.View);
                            // Repaint will deliver another BGRA buffer through Paint.
                            Interlocked.Exchange(ref dirty, 1);
                        }
                    }
                }
                if (Stopwatch.GetElapsedTime(lastTelemetryTicks, nowTicks).TotalSeconds >= 15)
                {
                    lastTelemetryTicks = nowTicks;
                    if (browser != null)
                        LogDiagnostic($"Frame pipeline: copied={Volatile.Read(ref paintCount)}, sent={Volatile.Read(ref sentFrames)}, ack={Volatile.Read(ref ackCount)}, credit={Volatile.Read(ref frameCredit)}, skippedPaint={Volatile.Read(ref skippedPaints)}.");
                }
                if (Volatile.Read(ref frameCredit) != 0 && Interlocked.Exchange(ref dirty, 0) != 0)
                {
                    byte[]? pixels;
                    int w, h;
                    lock (paintLock)
                    {
                        pixels = latestPaint;
                        w = latestPaintWidth;
                        h = latestPaintHeight;
                        if (pixels != null)
                        {
                            // Reserve the only in-flight frame before releasing
                            // the paint lock, avoiding stale concurrent copies.
                            Interlocked.Exchange(ref frameCredit, 0);
                            latestPaint = null;
                        }
                    }
                    if (pixels == null)
                    {
                        Interlocked.Exchange(ref dirty, 1);
                    }
                    else
                    {
                        // Raw BGRA, local pipe. No Deflate compression/decompression.
                        writer.Write((byte)3);
                        writer.Write(w);
                        writer.Write(h);
                        writer.Write(pixels.Length);
                        writer.Write(pixels);
                        var count = Interlocked.Increment(ref sentFrames);
                        if (count <= 3 || count % 120 == 0)
                            LogDiagnostic($"Frame {count}: {w}x{h}, raw BGRA over local IPC.");
                    }
                }
                writer.Flush();
            }
            catch (IOException ex)
            {
                LogDiagnostic("Frame pipe closed: " + ex.GetType().Name);
                stop.Cancel();
                break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Avoid tight loops. Keep sensitive URLs out of the log.
                pendingStatus = "Frame capture failed (" + ex.GetType().Name + ")";
                LogDiagnostic("Frame capture failure: " + ex.GetType().Name);
                Interlocked.Exchange(ref dirty, 1);
            }
            // The old path silently reduced output to ~30 FPS until the user
            // clicked the browser. Keep the requested cadence on login screens too.
            try { await Task.Delay(Math.Max(16, 1000 / Volatile.Read(ref browserFrameRate)), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        if (!cefInitialized) return;
        // Shutdown is owned by Run() on the same thread that called Cef.Initialize().
    }
}

/// <summary>
/// Main-frame navigation can stay only at the selected game's direct URL/root.
/// Asset requests and authenticated API calls are not intercepted.
/// </summary>
internal sealed class GameNavigationGuard : RequestHandler
{
    private readonly Uri initial;
    public GameNavigationGuard(Uri initial) { this.initial = initial; }

    // CEF intercepts network navigations but does not cancel client-side route
    // changes such as history.pushState. Capture them before the game UI acts.
    // This is UI containment, not authentication or a replacement for server checks.
    internal const string FrontendNavigationLock = """
        (() => {
          if (window.__bryerGamesNavigationGuard) return;
          Object.defineProperty(window, '__bryerGamesNavigationGuard', { value: true });
          const initialHost = location.hostname.toLowerCase();
          const initialPath = location.pathname.toLowerCase();
          const allowed = value => {
            try {
              const target = new URL(value, location.href);
              return target.protocol === 'https:' &&
                target.hostname.toLowerCase() === initialHost &&
                (target.pathname === '/' || target.pathname.toLowerCase() === initialPath);
            } catch { return false; }
          };
          const showBlockedMessage = () => {
            const id = '__bryerGamesBlockedNotice';
            if (document.getElementById(id)) return;
            if (!document.body) return;
            const overlay = document.createElement('div');
            overlay.id = id;
            overlay.setAttribute('role', 'dialog');
            overlay.setAttribute('aria-modal', 'true');
            overlay.setAttribute('aria-label', 'External website blocked');
            Object.assign(overlay.style, {
              position: 'fixed', inset: '0', zIndex: '2147483647',
              background: 'rgba(0, 0, 0, .72)', display: 'flex',
              alignItems: 'center', justifyContent: 'center',
              padding: '16px', boxSizing: 'border-box', pointerEvents: 'auto'
            });
            const panel = document.createElement('div');
            Object.assign(panel.style, {
              maxWidth: '410px', width: '100%', padding: '24px 22px',
              borderRadius: '12px', border: '1px solid rgba(224,187,109,.55)',
              background: 'linear-gradient(145deg, rgba(27,49,37,.98), rgba(10,19,16,.98))',
              boxShadow: '0 20px 60px rgba(0,0,0,.60)',
              color: '#f7e9cd', fontFamily: 'sans-serif',
              fontSize: '14px', lineHeight: '1.6', textAlign: 'center'
            });
            const message = document.createElement('p');
            message.textContent = 'To access the whole website you need to do so in your actual browser.';
            Object.assign(message.style, { margin: '0 0 20px', fontSize: '15px' });
            const close = document.createElement('button');
            close.type = 'button'; close.textContent = 'Close';
            Object.assign(close.style, {
              minWidth: '96px', padding: '8px 20px', borderRadius: '7px',
              cursor: 'pointer', border: '1px solid #c4a15f',
              background: '#40543c', color: '#fff0d0', fontSize: '14px'
            });
            close.addEventListener('click', () => overlay.remove());
            overlay.addEventListener('click', e => { if (e.target === overlay) overlay.remove(); });
            overlay.addEventListener('keydown', e => {
              if (e.key === 'Escape') { e.preventDefault(); overlay.remove(); }
            });
            panel.append(message, close); overlay.appendChild(panel);
            document.body.appendChild(overlay); close.focus();
          };
          // Also callable by the native CEF navigation blocker.
          window.__bryerGamesShowBlockedNavigation = showBlockedMessage;
          for (const method of ['pushState', 'replaceState']) {
            const original = history[method];
            history[method] = function(state, title, url) {
              if (url != null && !allowed(url)) { showBlockedMessage(); return; }
              return original.apply(this, arguments);
            };
          }
          const exitLabel = /^(?:minigames|coffer\s+hall|inventory|home|(?:back|return|go)\s+(?:to\s+)?(?:minigames|coffer\s+hall|site|shop|home)|(?:exit|leave)\s+(?:the\s+)?(?:game|site))$/i;
          const exitControls = '.mines-exit, .nalah-exit, .orbit-back, .va-exit, '
            + '[aria-label="Close Lumenfall Plinko"], [aria-label="Close Black Prism"], '
            + '[aria-label="Close Whispering Library"]';
          document.addEventListener('click', event => {
            if (!(event.target instanceof Element)) return;
            const element = event.target.closest('a[href], button, [role="button"]');
            if (!element) return;
            const link = element.getAttribute('href');
            const text = (element.getAttribute('aria-label') || element.getAttribute('title') || element.textContent || '')
              .replace(/\s+/g, ' ').trim().replace(/^[^a-zA-Z]+/, '');
            if (element.matches(exitControls) || (link != null && !allowed(link)) || exitLabel.test(text)) {
              event.preventDefault(); event.stopImmediatePropagation(); showBlockedMessage();
            }
          }, true);
          window.addEventListener('message', event => {
            if (event.data && event.data.type === 'LUMENFALL_CLOSE') {
              event.stopImmediatePropagation(); showBlockedMessage();
            }
          }, true);
          document.addEventListener('submit', event => {
            const form = event.target;
            if (form instanceof HTMLFormElement && !allowed(form.action)) {
              event.preventDefault(); event.stopImmediatePropagation(); showBlockedMessage();
            }
          }, true);
          const nativeOpen = window.open;
          window.open = function(url, ...rest) {
            if (url == null || !allowed(url)) { showBlockedMessage(); return null; }
            return nativeOpen.call(window, url, ...rest);
          };
        })();
        """;

    public static bool Allowed(string? url, Uri initial)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.Host.Equals(initial.Host, StringComparison.OrdinalIgnoreCase)) return false;
        // The game may return to / after processing an access-code link.
        // Other site routes such as /minigames are always disallowed.
        return uri.AbsolutePath == "/" ||
            uri.AbsolutePath.Equals(initial.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Allowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mines.bybryer.com", "plinko.bybryer.com", "wheel.bybryer.com",
                "aviator.bybryer.com", "nalah.bybryer.com", "blackprism.bybryer.com",
                "library.bybryer.com",
            }.Contains(uri.Host) &&
            (uri.AbsolutePath == "/" || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: 1 });
    }

    // Reject navigation in ALL frames. The old guard only checked the main frame,
    // allowing an embedded frame to load the Coffer Hall or Minigames page.
    // XHR, CSS, JS, and image requests remain unaffected.
    private bool AllowedDocument(string? url, bool isMain)
    {
        if (Allowed(url, initial)) return true;
        // Chromium uses about:blank while constructing a child frame.
        if (!isMain && (url is "about:blank" or "about:srcdoc")) return true;
        if (isMain || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.Host.Equals(initial.Host, StringComparison.OrdinalIgnoreCase)) return false;

        // These are the actual game documents loaded as same-origin iframes in
        // MinigamePage/PlinkoFrame. A blanket deny here kept them black or at 93%.
        return initial.Host.ToLowerInvariant() switch
        {
            "plinko.bybryer.com" => uri.AbsolutePath.Equals("/games/plinko/index.html", StringComparison.OrdinalIgnoreCase),
            "blackprism.bybryer.com" => uri.AbsolutePath.Equals("/games/black-prism/index-v73.html", StringComparison.OrdinalIgnoreCase),
            "library.bybryer.com" => uri.AbsolutePath.Equals("/games/whispering-library/index-v969.html", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // A popup is generated in the Chromium page, not a separate browser/OS window.
    private static void ShowBlockedNavigation(IBrowser browser)
    {
        try { browser.MainFrame.ExecuteJavaScriptAsync("window.__bryerGamesShowBlockedNavigation?.();"); }
        catch { /* Never let a blocked navigation crash the browser host. */ }
    }

    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, bool userGesture, bool isRedirect)
    {
        var denied = !AllowedDocument(request.Url, frame.IsMain);
        if (denied && userGesture) ShowBlockedNavigation(browser);
        return denied;
    }

    // A second gate at network-request time blocks disallowed documents in
    // main and child frames. API/asset/XHR requests stay untouched.
    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser,
        IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload,
        string requestInitiator, ref bool disableDefaultHandling)
        => isNavigation && !AllowedDocument(request.Url, frame?.IsMain == true)
            ? new DeniedGameNavigationRequest()
            : null!;

    protected override bool OnOpenUrlFromTab(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        string targetUrl, WindowOpenDisposition targetDisposition, bool userGesture)
    {
        if (userGesture) ShowBlockedNavigation(browser);
        return true; // No popup or external site windows.
    }
}


/// <summary>Rejects a disallowed document navigation before it reaches the network.</summary>
internal sealed class DeniedGameNavigationRequest : ResourceRequestHandler
{
    protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser,
        IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback)
        => CefReturnValue.Cancel;
}

/// <summary>Games remain embedded. window.open/target=_blank cannot create extra browser windows.</summary>
internal sealed class GamePopupGuard : LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition,
        bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo,
        IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        if (userGesture)
        {
            try { browser.MainFrame.ExecuteJavaScriptAsync("window.__bryerGamesShowBlockedNavigation?.();"); }
            catch { }
        }
        return true;
    }
}
