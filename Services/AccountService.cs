/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BryersHideoutGames.Security;
using Dalamud.Plugin.Services;

namespace BryersHideoutGames.Services;

/// Validates the player's existing access with Cloudflare
public sealed class AccountService : IDisposable
{
    private const string BaseUrl = "https://api.bybryer.com";
    private static readonly Regex CodeFormat = new("^[A-Za-z0-9_-]{4,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(14) };
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource stopping = new();
    private int busy;
    private int polling;
    private long nextPollUtcTicks;
    private long lastBalanceCheckUtcTicks;
    private bool launcherWasVisible;
    private int consecutiveRefreshFailures;
    private bool disposed;
    private string sessionToken = string.Empty;
    private string status = "Enter your access code to unlock the games.";
    private long balance;
    private bool hasBalance;
    private bool authorized;

    public bool IsAuthorized => authorized;
    public bool IsBusy => Volatile.Read(ref busy) != 0;
    public string Status => status;
    public long? Balance => hasBalance ? balance : null;
    public static bool IsValidCodeSyntax(string value) => CodeFormat.IsMatch(value.Trim());

    public AccountService(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
        if (!config.UseSavedCodeForDirectLinks || string.IsNullOrEmpty(config.EncryptedAccessCode)) return;
        status = "Checking your saved account...";
        _ = RestoreAsync();
    }

    public void Apply(string rawCode)
    {
        if (IsBusy || authorized || disposed) return;
        var code = rawCode.Trim().ToUpperInvariant();
        if (!IsValidCodeSyntax(code))
        {
            status = "Enter a valid access code first.";
            return;
        }
        _ = ApplyAsync(code, false);
    }

    private async Task RestoreAsync()
    {
        string code;
        try { code = ProtectedAccessCode.Decrypt(config.EncryptedAccessCode); }
        catch (Exception ex)
        {
            log.Warning("[BryerGames] Stored access could not be decrypted ({Type}).", ex.GetType().Name);
            status = "Saved access unavailable. Apply your code again.";
            return;
        }
        if (!IsValidCodeSyntax(code)) { status = "Saved access is invalid. Apply your code again."; return; }
        await ApplyAsync(code, true);
    }

    private async Task ApplyAsync(string code, bool restoring)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        status = restoring ? "Restoring your account..." : "Validating with Bryer's Hideout...";
        try
        {
            // Reuse the existing authenticated session when possible.
            if (restoring && !string.IsNullOrWhiteSpace(config.EncryptedSessionToken))
            {
                try
                {
                    var existing = ProtectedAccessCode.Decrypt(config.EncryptedSessionToken);
                    var result = await FetchContextAsync(existing, stopping.Token);
                    if (result.HasSession && result.Balance.HasValue)
                    {
                        if (config.BrowserProfileId.Length != 32)
                        {
                            config.BrowserProfileId = Guid.NewGuid().ToString("N");
                            config.Save();
                        }
                        sessionToken = existing;
                        authorized = true;
                        hasBalance = true;
                        balance = result.Balance.Value;
                        lastBalanceCheckUtcTicks = DateTime.UtcNow.Ticks;
                        nextPollUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks;
                        status = "Access code applied.";
                        log.Information("[BryerGames] Existing account session restored (account identifier withheld).");
                        return;
                    }
                }
                catch (Exception ex) when (!stopping.IsCancellationRequested)
                {
                    log.Warning("[BryerGames] Existing session is no longer valid ({Type}); a fresh link will be requested.", ex.GetType().Name);
                }
            }

            using var anonymous = await PostAsync("/v1/auth/anonymous", "{}", null, stopping.Token);
            if (!anonymous.RootElement.TryGetProperty("session", out var session) ||
                !session.TryGetProperty("access_token", out var accessElement))
                throw new InvalidOperationException("Anonymous session response missing access token.");
            var token = accessElement.GetString() ?? string.Empty;
            if (!token.StartsWith("bcs_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected session token format.");

            using var claimed = await PostAsync("/v1/rpc/claim_access_code", JsonSerializer.Serialize(new { p_code = code }), token, stopping.Token);
            if (!claimed.RootElement.TryGetProperty("success", out var claimedValue) || claimedValue.ValueKind != JsonValueKind.True)
            {
                status = "Access code rejected. Check the code and Venue availability.";
                return;
            }
            var context = await FetchContextAsync(token, stopping.Token);
            if (!context.HasSession || !context.Balance.HasValue)
            {
                status = "Account could not be verified after applying the code.";
                return;
            }

            // Commit only after the backend confirms both ownership and the wallet.
            config.EncryptedAccessCode = ProtectedAccessCode.Encrypt(code);
            config.EncryptedSessionToken = ProtectedAccessCode.Encrypt(token);
            if (!restoring || config.BrowserProfileId.Length != 32)
                config.BrowserProfileId = Guid.NewGuid().ToString("N");
            config.UseSavedCodeForDirectLinks = true;
            config.Save();
            sessionToken = token;
            authorized = true;
            balance = context.Balance.Value;
            hasBalance = true;
            lastBalanceCheckUtcTicks = DateTime.UtcNow.Ticks;
            nextPollUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks;
            status = "Access code applied.";
            log.Information("[BryerGames] Access code verified by the Cloudflare backend (code withheld).");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        {
            status = "Could not verify the code. Check your connection and try again.";
            log.Warning("[BryerGames] Access validation HTTP failure ({Type}, status {Status}).", ex.GetType().Name, ex.StatusCode);
        }
        catch (Exception ex)
        {
            status = "Access verification unavailable. Try again.";
            log.Warning("[BryerGames] Access validation failed ({Type}).", ex.GetType().Name);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    /// Check this launcher about every 30s and use a slower 90s safety refresh in the game.
    public void Tick(bool launcherVisible, bool gameVisible)
    {
        var returnedToLauncher = launcherVisible && !launcherWasVisible;
        launcherWasVisible = launcherVisible;
        if ((!launcherVisible && !gameVisible) || !authorized || disposed || IsBusy || sessionToken.Length == 0) return;
        var now = DateTime.UtcNow.Ticks;
        if (returnedToLauncher && now - Interlocked.Read(ref lastBalanceCheckUtcTicks) >= TimeSpan.FromSeconds(15).Ticks &&
            Volatile.Read(ref consecutiveRefreshFailures) == 0)
            Interlocked.Exchange(ref nextPollUtcTicks, now);
        if (now < Interlocked.Read(ref nextPollUtcTicks)) return;
        if (Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;
        var interval = launcherVisible ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(90);
        Interlocked.Exchange(ref nextPollUtcTicks, now + interval.Ticks);
        _ = UpdateBalanceAsync();
    }

    private async Task UpdateBalanceAsync()
    {
        try
        {
            var context = await FetchContextAsync(sessionToken, stopping.Token);
            Interlocked.Exchange(ref lastBalanceCheckUtcTicks, DateTime.UtcNow.Ticks);
            if (context.HasSession && context.Balance.HasValue)
            {
                balance = context.Balance.Value;
                hasBalance = true;
                Volatile.Write(ref consecutiveRefreshFailures, 0);
            }
            else
            {
                hasBalance = false;
                status = "Account session expired. Use Log Off and apply the code again.";
                Volatile.Write(ref consecutiveRefreshFailures, 1);
                Interlocked.Exchange(ref nextPollUtcTicks, DateTime.UtcNow.AddMinutes(2).Ticks);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            hasBalance = false;
            var failures = Math.Min(5, Interlocked.Increment(ref consecutiveRefreshFailures));
            // 30s, 60s, 120s... up to 5 minutes, without repeatedly querying a failing Worker.
            var retrySeconds = Math.Min(300, 15 * (1 << failures));
            Interlocked.Exchange(ref nextPollUtcTicks, DateTime.UtcNow.AddSeconds(retrySeconds).Ticks);
            log.Warning("[BryerGames] Balance refresh failed ({Type}); next attempt after {Seconds}s.", ex.GetType().Name, retrySeconds);
        }
        finally { Volatile.Write(ref polling, 0); }
    }

    private async Task<(bool HasSession, long? Balance)> FetchContextAsync(string token, CancellationToken cancellation)
    {
        using var data = await PostAsync("/v1/rpc/get_my_access_context", "{}", token, cancellation);
        var root = data.RootElement;
        if (!root.TryGetProperty("has_session", out var session) || session.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("player", out var player) || player.ValueKind != JsonValueKind.Object ||
            !player.TryGetProperty("gil_balance", out var gil))
            return (false, null);
        long balance = -1;
        if (gil.ValueKind == JsonValueKind.Number) gil.TryGetInt64(out balance);
        else if (gil.ValueKind == JsonValueKind.String)
            long.TryParse(gil.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out balance);
        return (true, balance >= 0 ? balance : null);
    }

    private async Task<JsonDocument> PostAsync(string relative, string json, string? bearer, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl + relative));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(bearer)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Request rejected by backend.", null, response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
    }

    public void LogOff()
    {
        if (disposed) return;
        var previous = sessionToken;
        sessionToken = string.Empty;
        authorized = false;
        hasBalance = false;
        config.EncryptedAccessCode = string.Empty;
        config.EncryptedSessionToken = string.Empty;
        config.BrowserProfileId = string.Empty;
        config.UseSavedCodeForDirectLinks = false;
        config.Save();
        Interlocked.Exchange(ref nextPollUtcTicks, 0);
        Interlocked.Exchange(ref lastBalanceCheckUtcTicks, 0);
        Volatile.Write(ref consecutiveRefreshFailures, 0);
        status = "Logged off. Enter your access code to play.";
        log.Information("[BryerGames] Local account link cleared.");
        if (!string.IsNullOrEmpty(previous))
            _ = Task.Run(async () =>
            {
                try { using var result = await PostAsync("/v1/auth/signout", "{}", previous, stopping.Token); }
                catch { /* The local link is removed even if the network is offline. */ }
            });
    }

    public void Dispose()
    {
        disposed = true;
        stopping.Cancel();
        http.Dispose();
        stopping.Dispose();
    }
}
