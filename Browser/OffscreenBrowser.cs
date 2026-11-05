/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace BryersHideoutGames.Browser;

/// ImGui/IPC side only. CefSharp assemblies live exclusively in the external executable.
public sealed class OffscreenBrowser : IDisposable
{
    private readonly IPluginLog log;
    private readonly int fpsLimit;
    private readonly int renderScalePercent;
    private readonly ITextureProvider textureProvider;
    private readonly string profileId;
    private readonly Channel<string> commands = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource stopping = new();
    private readonly object latestLock = new();
    private Process? helper;
    private NamedPipeClientStream? commandPipe;
    private NamedPipeClientStream? framePipe;
    private IDalamudTextureWrap? texture;
    private Task<IDalamudTextureWrap>? pendingUpload;
    private Frame? latest;
    private volatile bool connected;
    private volatile bool disposed;
    private bool mouseOver;
    private bool hasFocus;
    private bool leftMouseHeld;
    private bool rightMouseHeld;
    private bool middleMouseHeld;
    // reduces expensive full-frame readback, IPC traffic, and GPU uploads.
    private int width = 1000;
    private int height = 739;
    private int resizeCandidateWidth = 1000;
    private int resizeCandidateHeight = 739;
    private long resizeCandidateSince;
    private long lastMouseSendTicks;
    private int lastMouseX = -1;
    private int lastMouseY = -1;
    private long lastKeyboardPollTicks;
    private readonly HashSet<int> heldKeys = [];
    private string status = "Launching browser helper...";
    private int receivedFrames;
    private int uploadedFrames;
    private long pendingUploadStarted;
    private int pendingUploadWidth;
    private int pendingUploadHeight;
    private long lastUploadWarning;
    private long lastHealthReport;
    private Uri targetUrl;

    private sealed record Frame(int Width, int Height, byte[] Pixels);

    public OffscreenBrowser(Uri url, SiteDestination destination, IPluginLog log, ITextureProvider textureProvider, ChromiumRuntime runtime, int fpsLimit, int renderScalePercent, string profileId, bool compactMode)
    {
        if (profileId.Length != 32 || !System.Text.RegularExpressions.Regex.IsMatch(profileId, "^[0-9a-fA-F]{32}$"))
            throw new InvalidOperationException("Account browser profile was not initialized.");
        this.profileId = profileId;
        targetUrl = url;
        this.log = log;
        this.textureProvider = textureProvider;
        this.fpsLimit = Math.Clamp(fpsLimit, 30, 60);
        this.renderScalePercent = Math.Clamp(renderScalePercent, 55, 100);
        if (compactMode)
        {
            // Start Chromium at a portrait viewport before the first
            // UI draw. Responsive mobile CSS is active from the first load.
            width = resizeCandidateWidth = 430;
            height = resizeCandidateHeight = 755;
        }
        log.Information("[BryerGames] v13 direct game renderer active: {Destination}.", destination);
        _ = Task.Run(() => RunAsync(destination, runtime));
    }

    private async Task RunAsync(SiteDestination destination, ChromiumRuntime runtime)
    {
        var token = stopping.Token;
        var normalHelperExit = false;
        try
        {
            var pipeId = $"bryer-{Guid.NewGuid():N}";
            var profile = Path.Combine(runtime.ProfileRoot, "Games", profileId);
            Directory.CreateDirectory(profile);
            var psi = new ProcessStartInfo(runtime.HostExecutable)
            {
                WorkingDirectory = Path.GetDirectoryName(runtime.HostExecutable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(pipeId);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(profile); // Access codes are sent over the private pipe
            helper = Process.Start(psi) ?? throw new InvalidOperationException("Could not launch external browser process.");
            log.Information("[BryerGames] Launched isolated Chromium helper for {Destination} (PID {ProcessId}).", destination, helper.Id);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            commandPipe = new NamedPipeClientStream(".", pipeId + "-commands", PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            framePipe = new NamedPipeClientStream(".", pipeId + "-frames", PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await Task.WhenAll(commandPipe.ConnectAsync(timeout.Token), framePipe.ConnectAsync(timeout.Token));
            connected = true;
            commands.Writer.TryWrite(JsonSerializer.Serialize(new { type = "open", url = Volatile.Read(ref targetUrl).AbsoluteUri, fps = fpsLimit }));
            commands.Writer.TryWrite(JsonSerializer.Serialize(new { type = "resize", width, height }));
            var writer = SendCommandsAsync(commandPipe, token);
            await ReadFramesAsync(framePipe, token);
            commands.Writer.TryComplete();
            await writer;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (disposed || token.IsCancellationRequested)
        {
            // Local close/cancellation deliberately interrupts the pipe reader.
        }
        catch (Exception ex)
        {
            // The helper can finish normally before the pipe reader observes EOF.
            // Keep genuine crashes visible
            var exitCode = -1;
            try
            {
                if (helper != null)
                {
                    if (!helper.HasExited && ex is EndOfStreamException)
                        helper.WaitForExit(350);
                    if (helper.HasExited) exitCode = helper.ExitCode;
                }
            }
            catch { }
            if (disposed || token.IsCancellationRequested ||
                (ex is EndOfStreamException && exitCode == 0))
            {
                normalHelperExit = true;
                log.Information("[BryerGames] Browser helper pipe closed normally (exit={Code}).", exitCode);
            }
            else
            {
                status = "Browser helper disconnected. See /xllog.";
                // Only our own fixed-format protocol errors are surfaced; never log
                // URLs or arbitrary helper status text, which could contain access codes.
                var detail = ex is InvalidDataException dataError ? dataError.Message : ex.GetType().Name;
                log.Error("[BryerGames] Browser IPC failed ({Type}): {Detail}; helper exit={Exit}. Diagnostics: {LogFile}",
                    ex.GetType().Name, detail, exitCode < 0 ? "running/unknown" : exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Path.Combine(runtime.ProfileRoot, "Games", profileId, "browser-helper.log"));
            }
        }
        finally
        {
            connected = false;
            commands.Writer.TryComplete();
            if (!disposed && !token.IsCancellationRequested && !normalHelperExit)
            {
                var exit = helper?.HasExited == true ? $" (exit code {helper.ExitCode})" : string.Empty;
                status = "Chromium helper disconnected" + exit + ". See /xllog.";
                log.Warning("[BryerGames] Browser helper disconnected{Exit}.", exit);
                stopping.Cancel();
                try { if (helper is { HasExited: false }) helper.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private async Task SendCommandsAsync(Stream output, CancellationToken token)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await foreach (var message in commands.Reader.ReadAllAsync(token))
            await writer.WriteLineAsync(message.AsMemory(), token);
    }

    private async Task ReadFramesAsync(Stream input, CancellationToken token)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        // The first message must identify the helper protocol. Without this,
        // two different build versions can disagree on the raw BGRA frame format.
        var helloType = reader.ReadByte();
        if (helloType != 4)
            throw new InvalidDataException($"IPC version mismatch: host sent message {helloType} before the v13 handshake. Rebuild and install BOTH the plugin and helper.");
        var magic = reader.ReadInt32();
        var protocolVersion = reader.ReadInt32();
        if (magic != unchecked((int)0x42525952) || protocolVersion != 13)
            throw new InvalidDataException($"IPC protocol mismatch: received version {protocolVersion}, expected 13. Reinstall both binaries.");
        log.Information("[BryerGames] IPC protocol v13 handshake verified.");
        while (!token.IsCancellationRequested)
        {
            var type = reader.ReadByte();
            if (type == 2)
            {
                var len = reader.ReadInt32();
                if (len < 0 || len > 2048) throw new InvalidDataException($"Invalid status frame length ({len}).");
                status = Encoding.UTF8.GetString(ReadExact(reader, len));
            }
            else if (type is 1 or 3)
            {
                int w = reader.ReadInt32(), h = reader.ReadInt32(), n = reader.ReadInt32();
                if (w < 1 || h < 1 || w > 4096 || h > 3072 || n < 1 || n > 48 * 1024 * 1024)
                    throw new InvalidDataException($"Invalid image frame header (type={type}, width={w}, height={h}, bytes={n}).");
                // Type 3 is raw BGRA over a local pipe, avoiding per-frame Deflate CPU cost.
                var pixels = new byte[checked(w * h * 4)];
                if (type == 3)
                {
                    if (n != pixels.Length) throw new InvalidDataException($"Invalid raw frame length: header={n}, expected={pixels.Length}.");
                    input.ReadExactly(pixels);
                }
                else
                {
                    // Compatibility with older helper builds during an incremental update.
                    var encoded = ReadExact(reader, n);
                    using var memory = new MemoryStream(encoded);
                    using var deflate = new System.IO.Compression.DeflateStream(memory, System.IO.Compression.CompressionMode.Decompress);
                    deflate.ReadExactly(pixels);
                }
                lock (latestLock) latest = new Frame(w, h, pixels); // Drop stale frames instead of growing a queue.
                Interlocked.Increment(ref receivedFrames);
            }
            else throw new InvalidDataException($"Unknown IPC frame type {type} (v13 protocol).");
            await Task.Yield();
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var data = reader.ReadBytes(count);
        if (data.Length != count) throw new EndOfStreamException("Browser helper disconnected during transfer.");
        return data;
    }

    private void Send(object item)
    {
        if (connected && !disposed)
            commands.Writer.TryWrite(JsonSerializer.Serialize(item));
    }

    public void Navigate(Uri target)
    {
        if (!Destinations.IsAllowedGameUrl(target.AbsoluteUri))
            throw new InvalidOperationException("Navigation outside direct minigame hosts is blocked.");
        Volatile.Write(ref targetUrl, target);
        Send(new { type = "open", url = target.AbsoluteUri, fps = fpsLimit });
    }

    public void Draw(float availableWidth, float availableHeight)
    {
        if (disposed) return;
        // Actual minigame viewport, capped for predictable CPU/GPU load in FFXIV.
        // The minigame overlays have their own responsive rules on these direct URLs.
        var scale = renderScalePercent / 100f;
        var dw = Math.Clamp((int)MathF.Round(availableWidth * scale), 320, 1850);
        var dh = Math.Clamp((int)MathF.Round(availableHeight * scale), 245, 1350);
        if (Math.Abs(dw - resizeCandidateWidth) > 2 || Math.Abs(dh - resizeCandidateHeight) > 2)
        {
            resizeCandidateWidth = dw;
            resizeCandidateHeight = dh;
            resizeCandidateSince = Stopwatch.GetTimestamp();
        }
        // Avoid sending hundreds of viewport resizes while the user drags a window edge.
        if ((Math.Abs(resizeCandidateWidth - width) > 2 || Math.Abs(resizeCandidateHeight - height) > 2) &&
            (!connected || Stopwatch.GetElapsedTime(resizeCandidateSince).TotalMilliseconds >=
               (ImGui.IsMouseDown(ImGuiMouseButton.Left) ? 125 : 55)))
        {
            width = resizeCandidateWidth;
            height = resizeCandidateHeight;
            Send(new { type = "resize", width, height });
        }
        UploadPending();
        var pos = ImGui.GetCursorScreenPos();
        var viewport = new Vector2(availableWidth, availableHeight);
        // ImGui.Image is non-interactive; dragging its pixels can move the entire
        // Dalamud window. Keep an active ImGui item beneath the rendered texture,
        // so slider dragging belongs to the browser and not to window movement.
        ImGui.InvisibleButton("##bryer-game-input-surface", viewport);
        if (texture != null)
            ImGui.GetWindowDrawList().AddImage(texture.Handle, pos, pos + viewport);
        else
            ImGui.GetWindowDrawList().AddText(pos + new Vector2(12, 12), 0xffdddddd, status);
        HandleInput(pos, availableWidth, availableHeight);
    }

    private void UploadPending()
    {
        if (pendingUpload is { IsCompleted: true } completed)
        {
            pendingUpload = null;
            try
            {
                var wrap = completed.GetAwaiter().GetResult();
                if (disposed || Math.Abs(pendingUploadWidth - width) > 2 || Math.Abs(pendingUploadHeight - height) > 2)
                {
                    wrap.Dispose();
                    if (!disposed) Send(new { type = "frame-ack" });
                }
                else
                {
                    var old = texture;
                    texture = wrap;
                    old?.Dispose();
                    var count = Interlocked.Increment(ref uploadedFrames);
                    if (count == 1 || count == 3 || count % 100 == 0)
                        log.Information("[BryerGames] Browser frame {Count} uploaded ({Width}x{Height}).", count, (int)wrap.Width, (int)wrap.Height);
                    Send(new { type = "frame-ack" });
                }
            }
            catch (Exception ex)
            {
                status = "Could not upload browser image.";
                log.Warning("[BryerGames] GPU texture upload failed ({Type}).", ex.GetType().Name);
                // Unblock the remote renderer so a transient failure can recover.
                Send(new { type = "frame-ack" });
            }
        }
        if (pendingUpload != null)
        {
            var now = Stopwatch.GetTimestamp();
            if (pendingUploadStarted != 0 && Stopwatch.GetElapsedTime(pendingUploadStarted, now).TotalSeconds > 8 &&
                (lastUploadWarning == 0 || Stopwatch.GetElapsedTime(lastUploadWarning, now).TotalSeconds >= 15))
            {
                lastUploadWarning = now;
                log.Warning("[BryerGames] Texture upload still pending after 8s; received={Received}, uploaded={Uploaded}.",
                    Volatile.Read(ref receivedFrames), Volatile.Read(ref uploadedFrames));
            }
            return;
        }
        Frame? frame;
        lock (latestLock) { frame = latest; latest = null; }
        if (frame != null)
        {
            if (Math.Abs(frame.Width - width) > 2 || Math.Abs(frame.Height - height) > 2)
            {
                Send(new { type = "frame-ack" }); // Old viewport frame, request the new one.
                return;
            }
            pendingUploadWidth = frame.Width;
            pendingUploadHeight = frame.Height;
            pendingUploadStarted = Stopwatch.GetTimestamp();
            try
            {
                pendingUpload = textureProvider.CreateFromRawAsync(RawImageSpecification.Bgra32(frame.Width, frame.Height), frame.Pixels, "BryerGames helper frame");
            }
            catch (Exception ex)
            {
                log.Warning("[BryerGames] Could not start GPU texture upload ({Type}).", ex.GetType().Name);
                Send(new { type = "frame-ack" });
            }
        }
        var reportTicks = Stopwatch.GetTimestamp();
        if (connected && (lastHealthReport == 0 || Stopwatch.GetElapsedTime(lastHealthReport, reportTicks).TotalSeconds >= 20))
        {
            lastHealthReport = reportTicks;
            log.Information("[BryerGames] Frame pipeline: received={Received}, GPU uploaded={Uploaded}, upload pending={Pending}.",
                Volatile.Read(ref receivedFrames), Volatile.Read(ref uploadedFrames), pendingUpload != null);
        }
    }

    private void HandleInput(Vector2 origin, float displayWidth, float displayHeight)
    {
        if (!connected) return;
        var position = ImGui.GetMousePos();
        var inside = position.X >= origin.X && position.X < origin.X + displayWidth &&
                     position.Y >= origin.Y && position.Y < origin.Y + displayHeight;
        // Active ImGui items are not always reported as hovered during drag.
        // Retain capture until mouse-up, even if the pointer leaves the window.
        var dragHeld = leftMouseHeld || rightMouseHeld || middleMouseHeld;
        var hovered = inside && (ImGui.IsItemHovered() || ImGui.IsItemActive() || dragHeld);
        var capture = hovered || dragHeld;
        var x = Math.Clamp((int)((position.X - origin.X) * width / Math.Max(1f, displayWidth)), 0, width - 1);
        var y = Math.Clamp((int)((position.Y - origin.Y) * height / Math.Max(1f, displayHeight)), 0, height - 1);
        var now = Stopwatch.GetTimestamp();
        var mouseChanged = x != lastMouseX || y != lastMouseY;
        if (capture && (hovered != mouseOver || mouseChanged &&
            Stopwatch.GetElapsedTime(lastMouseSendTicks, now).TotalMilliseconds >= 8))
        {
            Send(new { type = "move", x, y, outside = !inside && !dragHeld });
            lastMouseSendTicks = now;
            lastMouseX = x;
            lastMouseY = y;
        }
        else if (!capture && mouseOver)
        {
            Send(new { type = "move", x, y, outside = true });
        }
        mouseOver = hovered;

        // Focus first so the following mouse-down reaches an active CEF view.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            hasFocus = true;
            Send(new { type = "focus", value = true });
        }
        // Click-down is sent only over the browser surface, but click-up is
        // always sent for a captured button so CEF never gets stuck dragging.
        SendMouse(ImGuiMouseButton.Left, "left", x, y, hovered, ref leftMouseHeld);
        SendMouse(ImGuiMouseButton.Right, "right", x, y, hovered, ref rightMouseHeld);
        SendMouse(ImGuiMouseButton.Middle, "middle", x, y, hovered, ref middleMouseHeld);

        if (hovered)
        {
            var wheel = ImGui.GetIO().MouseWheel;
            var wheelH = ImGui.GetIO().MouseWheelH;
            if (wheel != 0 || wheelH != 0)
                Send(new { type = "wheel", x, y, vertical = (int)(wheel * 120), horizontal = (int)(wheelH * 120) });
        }
        else if (!dragHeld && hasFocus && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            hasFocus = false;
            Send(new { type = "focus", value = false });
            heldKeys.Clear();
        }
        if (hasFocus && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
            Stopwatch.GetElapsedTime(lastKeyboardPollTicks, now).TotalMilliseconds >= 12)
        {
            lastKeyboardPollTicks = now;
            ForwardKeyboard();
        }
    }

    private void SendMouse(ImGuiMouseButton button, string name, int x, int y, bool hovered, ref bool held)
    {
        if (!held && hovered && ImGui.IsMouseClicked(button))
        {
            held = true;
            Send(new { type = "click", button = name, down = true, x, y });
        }
        if (held && (ImGui.IsMouseReleased(button) || !ImGui.IsMouseDown(button)))
        {
            held = false;
            Send(new { type = "click", button = name, down = false, x, y });
        }
    }

    private void ForwardKeyboard()
    {
        var keyboard = new byte[256];
        if (!Native.GetKeyboardState(keyboard)) return;
        var ctrl = (keyboard[0x11] & 0x80) != 0;
        var shift = (keyboard[0x10] & 0x80) != 0;
        var alt = (keyboard[0x12] & 0x80) != 0;
        var modifiers = (ctrl ? 1 : 0) | (shift ? 2 : 0) | (alt ? 4 : 0);
        for (var vk = 8; vk < 255; vk++)
        {
            if (vk is 16 or 17 or 18 or 91 or 92) continue;
            if (vk >= 112 && vk <= 123) continue;
            var down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down == heldKeys.Contains(vk)) continue;
            if (down) heldKeys.Add(vk); else heldKeys.Remove(vk);
            var scan = (int)Native.MapVirtualKey((uint)vk, 0);
            Send(new { type = "key", vk, scan, down, modifiers });
            if (!down || ctrl || alt) continue;
            var characters = new StringBuilder(8);
            var count = Native.ToUnicode((uint)vk, (uint)scan, keyboard, characters, characters.Capacity, 0);
            for (var i = 0; i < count && i < characters.Length; i++)
                Send(new { type = "char", code = (int)characters[i], scan, modifiers });
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        // Flag local closure BEFORE tearing down pipes so EOF is expected, not ERR.
        disposed = true;
        // Ask Chromium to exit through IPC before any forced process termination.
        if (connected) commands.Writer.TryWrite("{\"type\":\"exit\"}");
        commands.Writer.TryComplete();
        connected = false;
        // Close the receiving pipe immediately: blocking reads must not outlive
        // the ImGui window or get logged as unexpected helper disconnects.
        try { framePipe?.Dispose(); } catch { }
        texture?.Dispose();
        texture = null;
        var upload = pendingUpload;
        pendingUpload = null;
        if (upload != null)
            _ = upload.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
        // Closing pipes unblocks frame reads. The host has a parent watchdog as a fallback.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                stopping.Cancel();
                commandPipe?.Dispose();
                framePipe?.Dispose();
                var proc = helper;
                if (proc != null)
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                    proc.Dispose();
                }
            }
            catch { }
        });
    }

    private static class Native
    {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetKeyboardState([Out] byte[] keys);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint virtualKey, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ToUnicode(uint virtualKey, uint scanCode, byte[] keys, StringBuilder characters, int capacity, uint flags);
    }
}
