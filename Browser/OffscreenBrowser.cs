/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Buffers;
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

/// <summary>
/// ImGui/IPC side only. CefSharp assemblies live exclusively in the external executable.
/// </summary>
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
    // The normal renderer uses two persistent, CPU-writable D3D11 textures.
    // Legacy per-frame creation remains an emergency fallback for incompatible GPUs.
    private PersistentGpuTextures? gpuSurfaces;
    private bool legacyTextureUploads;
    private int gpuMapSkipped;
    private Task<IDalamudTextureWrap>? pendingUpload;
    // Keep rented pixels alive until CreateFromRawAsync has finished consuming them.
    private Frame? uploadingFrame;
    private Frame? latest;
    private volatile bool connected;
    private volatile bool disposed;
    private bool mouseOver;
    // Only an explicit click on the web canvas grants keyboard ownership.
    // Never steal keys merely because the browser window is visible.
    private volatile bool hasFocus;
    // Framework.Update may request a blur when FFXIV loses OS foreground.
    // It never touches the input HashSets from another callback.
    private int focusReleaseRequested;
    private readonly HashSet<int> ignoreUntilReleased = [];

    public bool CapturesKeyboard => !disposed && connected && hasFocus && IsGameWindowForeground();

    public void RequestKeyboardFocusRelease()
    {
        if (hasFocus) Interlocked.Exchange(ref focusReleaseRequested, 1);
    }

    public void ReleaseFocusFromWindow() => ReleaseKeyboardFocus();

    private static bool IsGameWindowForeground()
    {
        var active = Native.GetForegroundWindow();
        if (active == IntPtr.Zero) return false;
        _ = Native.GetWindowThreadProcessId(active, out var pid);
        return pid == (uint)Environment.ProcessId;
    }
    private bool leftMouseHeld;
    private bool rightMouseHeld;
    private bool middleMouseHeld;
    // The remote Chromium surface is deliberately smaller than the ImGui rectangle.
    // This reduces expensive full-frame readback, IPC traffic, and GPU uploads.
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
    // Replaced frames were received successfully but deliberately not uploaded:
    // a real-time animation should display the newest frame, never build a queue.
    private int replacedBeforeUpload;
    private long pendingUploadStarted;
    private int pendingUploadWidth;
    private int pendingUploadHeight;
    private long lastUploadWarning;
    private long lastHealthReport;
    private long totalUploadMicroseconds;
    private long completedUploadSamples;
    private readonly string telemetryFilePath;
    private int drawCount;
    private int previousDrawCount;
    private int previousReceivedCount;
    private int previousUploadedCount;
    private int previousReplacedCount;
    private long previousUploadSamples;
    private long previousUploadMicroseconds;
    private Uri targetUrl;

    private sealed record Frame(int Width, int Height, byte[] Pixels);

    public OffscreenBrowser(Uri url, SiteDestination destination, IPluginLog log, ITextureProvider textureProvider, ChromiumRuntime runtime, int fpsLimit, int renderScalePercent, string profileId, bool compactMode)
    {
        if (profileId.Length != 32 || !System.Text.RegularExpressions.Regex.IsMatch(profileId, "^[0-9a-fA-F]{32}$"))
            throw new InvalidOperationException("Account browser profile was not initialized.");
        this.profileId = profileId;
        telemetryFilePath = Path.Combine(runtime.ProfileRoot, "Games", profileId, "plugin-frame-pipeline.log");
        targetUrl = url;
        this.log = log;
        this.textureProvider = textureProvider;
        this.fpsLimit = Math.Clamp(fpsLimit, 30, 60);
        this.renderScalePercent = Math.Clamp(renderScalePercent, 55, 100);
        if (compactMode)
        {
            // Start Chromium at a narrow portrait viewport before the first
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
            psi.ArgumentList.Add(profile); // Access codes are sent over the private pipe, NEVER via command-line args.
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
            // Keep genuine crashes visible, but don't report an expected close as ERR.
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
        {
            var nextMessage = message;
            // Mouse moves can arrive faster than the pipe/CEF renderer can
            // process them. Collapse only consecutive moves, preserving the
            // order of clicks, releases, wheel events and keystrokes.
            if (message.StartsWith("{\"type\":\"move\"", StringComparison.Ordinal))
            {
                while (commands.Reader.TryPeek(out var next) &&
                       next.StartsWith("{\"type\":\"move\"", StringComparison.Ordinal) &&
                       commands.Reader.TryRead(out var fresher))
                    nextMessage = fresher;
            }
            await writer.WriteLineAsync(nextMessage.AsMemory(), token);
        }
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
                // Reuse large (>85 KB) frame buffers instead of allocating
                // on the Large Object Heap at every browser refresh.
                var byteCount = checked(w * h * 4);
                byte[]? pixels = ArrayPool<byte>.Shared.Rent(byteCount);
                Frame? replaced = null;
                try
                {
                    if (type == 3)
                    {
                        if (n != byteCount) throw new InvalidDataException($"Invalid raw frame length: header={n}, expected={byteCount}.");
                        input.ReadExactly(pixels.AsSpan(0, byteCount));
                    }
                    else
                    {
                        var encoded = ReadExact(reader, n);
                        using var memory = new MemoryStream(encoded);
                        using var deflate = new System.IO.Compression.DeflateStream(memory, System.IO.Compression.CompressionMode.Decompress);
                        deflate.ReadExactly(pixels.AsSpan(0, byteCount));
                    }
                    lock (latestLock)
                    {
                        if (!disposed)
                        {
                            replaced = latest;
                            latest = new Frame(w, h, pixels);
                            pixels = null; // transferred to the newest pending frame
                            Interlocked.Increment(ref receivedFrames);
                        }
                    }
                }
                finally
                {
                    if (pixels != null) ArrayPool<byte>.Shared.Return(pixels);
                    if (replaced != null)
                    {
                        Interlocked.Increment(ref replacedBeforeUpload);
                        ArrayPool<byte>.Shared.Return(replaced.Pixels);
                    }
                }
                // Return transport credit as soon as the complete bitmap has been
                // received and retained. Do NOT wait for the asynchronous GPU upload
                // or another ImGui Draw. Only one raw frame may be in the pipe at once;
                // the consumer keeps at most one READY frame plus one UPLOADING frame.
                // This removes an entire render/upload/Draw round-trip from the
                // Chromium pipeline while keeping memory usage bounded.
                Send(new { type = "frame-ack" });
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
        ReleaseKeyboardFocus();
        Send(new { type = "open", url = target.AbsoluteUri, fps = fpsLimit });
    }

    public void Draw(float availableWidth, float availableHeight)
    {
        if (disposed) return;
        Interlocked.Increment(ref drawCount);
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
        ReportPipelineMetrics();
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
        if (!legacyTextureUploads)
        {
            UploadIntoReusableTexture();
            return;
        }
        UploadPendingLegacy();
    }

    /// <summary>
    /// Runs on Dalamud's ImGui draw thread. Map(WRITE_DISCARD) writes into a
    /// reusable D3D11 dynamic texture; there is no per-frame GPU allocation or
    /// asynchronous texture-loader queue. The other texture is used for display.
    /// </summary>
    private void UploadIntoReusableTexture()
    {
        Frame? frame;
        lock (latestLock) { frame = latest; latest = null; }
        if (frame == null) return;

        var transferFrameToFallback = false;
        try
        {
            if (disposed || Math.Abs(frame.Width - width) > 2 || Math.Abs(frame.Height - height) > 2)
                return;

            if (gpuSurfaces == null || gpuSurfaces.Width != frame.Width || gpuSurfaces.Height != frame.Height)
            {
                // Allocate only on initial load or a real resolution change.
                var newSurfaces = new PersistentGpuTextures(textureProvider, frame.Width, frame.Height);
                var oldSurfaces = gpuSurfaces;
                gpuSurfaces = newSurfaces;
                texture = null;
                oldSurfaces?.Dispose();
                log.Information("[BryerGames] Created two reusable D3D11 textures ({Width}x{Height}).", frame.Width, frame.Height);
            }

            var started = Stopwatch.GetTimestamp();
            if (gpuSurfaces.TryUpdate(frame.Pixels.AsSpan(0, checked(frame.Width * frame.Height * 4))))
            {
                texture = gpuSurfaces.Current;
                Interlocked.Add(ref totalUploadMicroseconds,
                    (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds);
                Interlocked.Increment(ref completedUploadSamples);
                Interlocked.Increment(ref uploadedFrames);
            }
            else
            {
                // Defensive handling if a driver reports a busy resource despite MapFlags=0.
                Interlocked.Increment(ref gpuMapSkipped);
            }
        }
        catch (Exception ex)
        {
            // Restore the previous, known-working renderer when D3D11 cannot
            // supply a writable surface. The failure is also written to the
            // user's standalone diagnostic file (no /xllog needed).
            legacyTextureUploads = true;
            texture = null;
            gpuSurfaces?.Dispose();
            gpuSurfaces = null;
            log.Warning("[BryerGames] D3D11 dynamic texture path unavailable ({Type}); using legacy texture uploader.", ex.GetType().Name);
            AppendRendererDiagnostic("D3D11 fallback: " + ex.GetType().Name + " (" + ex.HResult.ToString("X8") + "): " + ex.Message);
            transferFrameToFallback = true;
        }
        finally
        {
            if (transferFrameToFallback)
            {
                // Return this valid frame to the ready slot for the legacy path.
                Frame? displaced;
                lock (latestLock)
                {
                    displaced = latest;
                    latest = frame;
                }
                if (displaced != null)
                {
                    Interlocked.Increment(ref replacedBeforeUpload);
                    ArrayPool<byte>.Shared.Return(displaced.Pixels);
                }
            }
            else ArrayPool<byte>.Shared.Return(frame.Pixels);
        }
    }

    private void AppendRendererDiagnostic(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
        _ = Task.Run(() =>
        {
            try { File.AppendAllText(telemetryFilePath, line); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }

    private void UploadPendingLegacy()
    {
        if (pendingUpload is { IsCompleted: true } completed)
        {
            pendingUpload = null;
            var completedPixels = uploadingFrame;
            uploadingFrame = null;
            try
            {
                var wrap = completed.GetAwaiter().GetResult();
                if (pendingUploadStarted != 0)
                {
                    Interlocked.Add(ref totalUploadMicroseconds,
                        (long)Stopwatch.GetElapsedTime(pendingUploadStarted).TotalMicroseconds);
                    Interlocked.Increment(ref completedUploadSamples);
                }
                if (disposed || Math.Abs(pendingUploadWidth - width) > 2 || Math.Abs(pendingUploadHeight - height) > 2)
                {
                    wrap.Dispose();
                }
                else
                {
                    var old = texture;
                    texture = wrap;
                    old?.Dispose();
                    var count = Interlocked.Increment(ref uploadedFrames);
                    if (count == 1 || count == 3 || count % 100 == 0)
                        log.Information("[BryerGames] Browser frame {Count} uploaded ({Width}x{Height}).", count, (int)wrap.Width, (int)wrap.Height);
                }
            }
            catch (Exception ex)
            {
                status = "Could not upload browser image.";
                log.Warning("[BryerGames] GPU texture upload failed ({Type}).", ex.GetType().Name);
                // Transport credit was already returned by ReadFramesAsync.
            }
            finally
            {
                if (completedPixels != null) ArrayPool<byte>.Shared.Return(completedPixels.Pixels);
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
                ArrayPool<byte>.Shared.Return(frame.Pixels);
                // Transport credit was returned when this frame was received.
                return;
            }
            pendingUploadWidth = frame.Width;
            pendingUploadHeight = frame.Height;
            pendingUploadStarted = Stopwatch.GetTimestamp();
            try
            {
                // A pooled array may have spare capacity; upload only valid pixels.
                uploadingFrame = frame;
                pendingUpload = textureProvider.CreateFromRawAsync(
                    RawImageSpecification.Bgra32(frame.Width, frame.Height),
                    frame.Pixels.AsMemory(0, checked(frame.Width * frame.Height * 4)),
                    "BryerGames helper frame");
            }
            catch (Exception ex)
            {
                uploadingFrame = null;
                ArrayPool<byte>.Shared.Return(frame.Pixels);
                log.Warning("[BryerGames] Could not start GPU texture upload ({Type}).", ex.GetType().Name);
            }
        }
    }

    // Separate, user-readable diagnostics file: /xllog may filter Information logs.
    // Collected from Draw so a stalled texture upload still produces a report.
    private void ReportPipelineMetrics()
    {
        if (!connected) return;
        var now = Stopwatch.GetTimestamp();
        if (lastHealthReport == 0)
        {
            lastHealthReport = now;
            previousDrawCount = Volatile.Read(ref drawCount);
            previousReceivedCount = Volatile.Read(ref receivedFrames);
            previousUploadedCount = Volatile.Read(ref uploadedFrames);
            previousReplacedCount = Volatile.Read(ref replacedBeforeUpload);
            previousUploadSamples = Interlocked.Read(ref completedUploadSamples);
            previousUploadMicroseconds = Interlocked.Read(ref totalUploadMicroseconds);
            return;
        }
        var seconds = Stopwatch.GetElapsedTime(lastHealthReport, now).TotalSeconds;
        if (seconds < 15) return;
        lastHealthReport = now;
        var draws = Volatile.Read(ref drawCount);
        var received = Volatile.Read(ref receivedFrames);
        var uploaded = Volatile.Read(ref uploadedFrames);
        var replaced = Volatile.Read(ref replacedBeforeUpload);
        var samples = Interlocked.Read(ref completedUploadSamples);
        var uploadMs = Interlocked.Read(ref totalUploadMicroseconds);
        var newSamples = samples - previousUploadSamples;
        var avgMs = newSamples > 0 ? (double)(uploadMs - previousUploadMicroseconds) / newSamples / 1000d : 0;
        var pendingMs = pendingUpload != null && pendingUploadStarted != 0 ?
            Stopwatch.GetElapsedTime(pendingUploadStarted, now).TotalMilliseconds : 0;
        var mode = legacyTextureUploads ? "legacy-create-per-frame" : "dynamic-d3d11-pingpong";
        var line = $"{DateTimeOffset.UtcNow:O} Frame pipeline 15s: renderer={mode}, gpuMapSkipped={Volatile.Read(ref gpuMapSkipped)}, drawFps={(draws - previousDrawCount) / seconds:F1}, receivedFps={(received - previousReceivedCount) / seconds:F1}, uploadedFps={(uploaded - previousUploadedCount) / seconds:F1}, replacedFps={(replaced - previousReplacedCount) / seconds:F1}, uploadAvgMs={avgMs:F1}, pendingUploadMs={pendingMs:F0}, resolution={width}x{height}.";
        previousDrawCount = draws;
        previousReceivedCount = received;
        previousUploadedCount = uploaded;
        previousReplacedCount = replaced;
        previousUploadSamples = samples;
        previousUploadMicroseconds = uploadMs;
        // Disk I/O must never block ImGui's render thread.
        _ = Task.Run(() =>
        {
            try { File.AppendAllText(telemetryFilePath, line + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }

    private void HandleInput(Vector2 origin, float displayWidth, float displayHeight)
    {
        if (!connected)
        {
            ReleaseKeyboardFocus();
            return;
        }
        // No background focus monitor or CEF scheduling changes: the existing
        // Draw and Framework.Update callbacks are sufficient for input gating.
        var foreground = IsGameWindowForeground();
        if (Interlocked.Exchange(ref focusReleaseRequested, 0) != 0 || !foreground)
            ReleaseKeyboardFocus();
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

        // Acquire keyboard focus only through a deliberate click on the web
        // surface, not when opening the game or simply hovering the plugin.
        if (foreground && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            AcquireKeyboardFocus();
        else if (hasFocus && !dragHeld &&
                 (ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
                  ImGui.IsMouseClicked(ImGuiMouseButton.Right) ||
                  ImGui.IsMouseClicked(ImGuiMouseButton.Middle)))
            ReleaseKeyboardFocus();
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
        if (hasFocus && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            ReleaseKeyboardFocus();
        if (!CapturesKeyboard) return;

        // Ask ImGui to capture keyboard input only for the focused web canvas.
        // IKeyState.ClearAll on the existing Framework.Update callback covers
        // the game's separate key state (and only runs while captured).
        ImGui.GetIO().WantCaptureKeyboard = true;
        if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)
        {
            // Esc immediately hands the keyboard back to FFXIV.
            ReleaseKeyboardFocus();
            return;
        }
        if (Stopwatch.GetElapsedTime(lastKeyboardPollTicks, now).TotalMilliseconds >= 12)
        {
            lastKeyboardPollTicks = now;
            ForwardKeyboard();
        }
    }

    private void AcquireKeyboardFocus()
    {
        if (hasFocus || !IsGameWindowForeground()) return;
        heldKeys.Clear();
        ignoreUntilReleased.Clear();
        // A key held BEFORE clicking the game is not a new web keypress.
        // Releasing it once is required before it can reach the browser.
        for (var vk = 8; vk < 255; vk++)
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0)
                ignoreUntilReleased.Add(vk);
        hasFocus = true;
        Send(new { type = "focus", value = true });
    }

    private void ReleaseKeyboardFocus()
    {
        if (!hasFocus) return;
        hasFocus = false;
        // Release remote key-downs explicitly, even when focus is lost while
        // a movement/bet key is held; otherwise CEF can retain a stuck key.
        foreach (var vk in heldKeys)
        {
            var scan = (int)Native.MapVirtualKey((uint)vk, 0);
            Send(new { type = "key", vk, scan, down = false, modifiers = 0 });
        }
        heldKeys.Clear();
        ignoreUntilReleased.Clear();
        Send(new { type = "focus", value = false });
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
        // Physical modifier state is more reliable than GetKeyboardState on
        // the Dalamud draw thread (notably for Ctrl+Shift+F8/F9 diagnostics).
        var ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
        var shift = (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;
        var alt = (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
        keyboard[0x11] = ctrl ? (byte)0x80 : (byte)0;
        keyboard[0x10] = shift ? (byte)0x80 : (byte)0;
        keyboard[0x12] = alt ? (byte)0x80 : (byte)0;
        var modifiers = (ctrl ? 1 : 0) | (shift ? 2 : 0) | (alt ? 4 : 0);
        for (var vk = 8; vk < 255; vk++)
        {
            if (vk is 16 or 17 or 18 or 91 or 92 or 160 or 161 or 162 or 163 or 164 or 165) continue;
            if (vk >= 112 && vk <= 123 && vk is not (119 or 120)) continue;
            var down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            if (ignoreUntilReleased.Contains(vk))
            {
                if (!down) ignoreUntilReleased.Remove(vk);
                continue;
            }
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
        ReleaseKeyboardFocus();
        // Flag local closure BEFORE tearing down pipes so EOF is expected, not ERR.
        disposed = true;
        // Ask Chromium to exit through IPC before any forced process termination.
        if (connected) commands.Writer.TryWrite("{\"type\":\"exit\"}");
        commands.Writer.TryComplete();
        connected = false;
        // Close the receiving pipe immediately: blocking reads must not outlive
        // the ImGui window or get logged as unexpected helper disconnects.
        try { framePipe?.Dispose(); } catch { }
        // gpuSurfaces owns both wraps in the optimized renderer. The legacy
        // renderer owns texture directly. Never dispose the same wrap twice.
        if (gpuSurfaces != null)
        {
            texture = null;
            gpuSurfaces.Dispose();
            gpuSurfaces = null;
        }
        else texture?.Dispose();
        texture = null;
        Frame? discarded;
        lock (latestLock) { discarded = latest; latest = null; }
        if (discarded != null) ArrayPool<byte>.Shared.Return(discarded.Pixels);
        var upload = pendingUpload;
        pendingUpload = null;
        var uploading = uploadingFrame;
        uploadingFrame = null;
        if (upload != null)
            _ = upload.ContinueWith(t =>
            {
                try
                {
                    if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose();
                    else _ = t.Exception;
                }
                finally { if (uploading != null) ArrayPool<byte>.Shared.Return(uploading.Pixels); }
            }, TaskScheduler.Default);
        else if (uploading != null) ArrayPool<byte>.Shared.Return(uploading.Pixels);
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
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetKeyboardState([Out] byte[] keys);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint virtualKey, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ToUnicode(uint virtualKey, uint scanCode, byte[] keys, StringBuilder characters, int capacity, uint flags);
    }
}
