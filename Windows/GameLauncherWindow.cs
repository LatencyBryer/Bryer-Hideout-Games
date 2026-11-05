/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using BryersHideoutGames.Browser;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;

namespace BryersHideoutGames.Windows;
public sealed class GameLauncherWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Task<IDalamudTextureWrap>? loadingAtlas;
    private IDalamudTextureWrap? atlas;
    private string enteredCode = string.Empty;
    private bool loadAttempted;
    private bool disposed;
    private bool showAccessNotice;
    private bool applyCompactWindowSize;

    // ABGR for ImDrawList.
    private const uint Gold = 0xff8fd9f7;
    private const uint Cream = 0xffecefe7;
    private const uint Muted = 0xffa0a79a;
    private const uint Border = 0xff547762;
    private const uint Deep = 0xff101a14;

    public GameLauncherWindow(Plugin plugin) : base("Bryer's Hideout - Games###BryerGameLauncherCompactV17")
    {
        this.plugin = plugin;
        Size = new Vector2(590, 415);
        applyCompactWindowSize = !plugin.Configuration.LauncherCompactSizeAppliedV17;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(550, 400),
            MaximumSize = new Vector2(960, 780),
        };
        RespectCloseHotkey = true;
    }

    public override void PreDraw()
    {
        // Existing ImGui window sizes are saved.
        // Allow the player's own resizing to persist on future sessions.
        if (applyCompactWindowSize)
        {
            Size = new Vector2(590, 415);
            SizeCondition = ImGuiCond.Always;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        if (!applyCompactWindowSize) return;
        applyCompactWindowSize = false;
        SizeCondition = ImGuiCond.FirstUseEver;
        plugin.Configuration.LauncherCompactSizeAppliedV17 = true;
        plugin.Configuration.Save();
    }

    public void RequireCode()
    {
        IsOpen = true;
        showAccessNotice = true;
    }

    private void EnsureAtlas()
    {
        if (!loadAttempted)
        {
            loadAttempted = true;
            try
            {
                var directory = Plugin.PluginInterface.AssemblyLocation.DirectoryName!;
                var bytes = File.ReadAllBytes(Path.Combine(directory, "minigame-icons.bgra"));
                if (bytes.Length != 512 * 256 * 4) throw new InvalidDataException("Invalid minigame icon atlas length.");
                loadingAtlas = plugin.TextureProvider.CreateFromRawAsync(
                    RawImageSpecification.Bgra32(512, 256), bytes, "BryerGames icons");
            }
            catch (Exception ex)
            {
                plugin.Log.Warning("[BryerGames] Icon atlas could not be loaded ({Type}).", ex.GetType().Name);
            }
        }
        if (loadingAtlas is not { IsCompleted: true } task) return;
        loadingAtlas = null;
        try { atlas = task.GetAwaiter().GetResult(); }
        catch (Exception ex) { plugin.Log.Warning("[BryerGames] Icon texture upload failed ({Type}).", ex.GetType().Name); }
    }

    public override void Draw()
    {
        EnsureAtlas();
        var origin = ImGui.GetCursorScreenPos();
        var region = ImGui.GetContentRegionAvail();
        var width = Math.Max(1f, region.X);
        var height = Math.Max(1f, region.Y);
        var columns = width >= 540f ? 4 : 3;
        var rows = (Destinations.All.Length + columns - 1) / columns;
        var draw = ImGui.GetWindowDrawList();

        PaintBackground(draw, origin, width, height);
        DrawHeader(draw, origin, width);

        const float leftPadding = 24f;
        const float gap = 14f;
        var innerWidth = width - leftPadding * 2;
        var cellWidth = (innerWidth - (columns - 1) * gap) / columns;
        var gridTop = origin.Y + 103f;
        var footerY = origin.Y + height - 70f;
        var gridSpace = Math.Max(1f, footerY - gridTop - 6f);
        var rowHeight = Math.Clamp(gridSpace / rows, 94f, 136f);
        if (gridSpace > rowHeight * rows)
            gridTop += (gridSpace - rowHeight * rows) * .48f;

        // Explicit screen coordinates to prevent SameLine/NewLine from breaking when ImGui styles/scales change.
        var games = Destinations.All;
        for (var row = 0; row < rows; row++)
        {
            var firstIndex = row * columns;
            var count = Math.Min(columns, games.Length - firstIndex);
            var rowWidth = count * cellWidth + (count - 1) * gap;
            var rowX = origin.X + (width - rowWidth) / 2;
            for (var col = 0; col < count; col++)
            {
                var gameOrigin = new Vector2(rowX + col * (cellWidth + gap), gridTop + row * rowHeight);
                DrawGame(draw, games[firstIndex + col], gameOrigin, cellWidth, rowHeight);
            }
        }

        PaintFooter(draw, origin, width, height, footerY);
        DrawAccessForm(origin, width, footerY + 17f);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - 1f, height - 1f));
        ImGui.Dummy(new Vector2(1f, 1f));
        if (showAccessNotice)
        {
            showAccessNotice = false;
            ImGui.OpenPopup("Access code required##BryerGames");
        }
        DrawAccessNotice();
    }

    private static void PaintBackground(ImDrawListPtr draw, Vector2 origin, float width, float height)
    {
        var bottom = origin + new Vector2(width, height);
        draw.AddRectFilledMultiColor(origin, bottom,
            0xff16251b, 0xff111b15, 0xff0e1512, 0xff17291e);
        // Quiet alternating bands give the panel some depth without a flat green background.
        draw.AddRectFilledMultiColor(origin + new Vector2(0, 0), origin + new Vector2(width, 101),
            0xff304937, 0xff203528, 0xff1b2e24, 0xff284331);
        draw.AddRectFilledMultiColor(origin + new Vector2(0, height - 70), bottom,
            0xff223528, 0xff162a20, 0xff111c17, 0xff192920);
        draw.AddLine(origin + new Vector2(20, 98), origin + new Vector2(width - 20, 98), Gold, 1.25f);
        draw.AddLine(origin + new Vector2(20, 100), origin + new Vector2(width - 20, 100), 0xff38533c, 1f);
        draw.AddRect(origin + new Vector2(9, 8), bottom - new Vector2(9, 9), 0xff385442, 8f, ImDrawFlags.None, 1f);
        for (var i = 0; i < 3; i++)
        {
            var inset = 20f + i * 7f;
            draw.AddLine(origin + new Vector2(inset, 23), origin + new Vector2(inset + 24, 23), Border, 1f);
            draw.AddLine(origin + new Vector2(width - inset - 24, 23), origin + new Vector2(width - inset, 23), Border, 1f);
        }
    }

    private void DrawHeader(ImDrawListPtr draw, Vector2 origin, float width)
    {
        var heading = "M I N I G A M E S";
        var headingSize = ImGui.CalcTextSize(heading);
        draw.AddText(origin + new Vector2((width - headingSize.X) / 2, 17f), Gold, heading);
        // Font Awesome question-circle (U+F059)
        ImGui.SetCursorScreenPos(origin + new Vector2(26f, 16f));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(Vector4.One, "\uf059");
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("If you need to make a deposit or want to cash out,\nplease talk to your Host at the venue.");

        const string label = "Your balance: ";
        var account = plugin.Account;
        var value = account.IsAuthorized
            ? account.Balance.HasValue
                ? account.Balance.Value.ToString("N0", CultureInfo.InvariantCulture) + " Gil"
                : "Updating..."
            : "—";
        var a = ImGui.CalcTextSize(label);
        var b = ImGui.CalcTextSize(value);
        var start = origin + new Vector2((width - a.X - b.X) / 2, 54f);
        draw.AddText(start + new Vector2(0, 1), 0xff0e1710, label);
        draw.AddText(start, Cream, label);
        draw.AddText(start + new Vector2(a.X + 0, 1), 0xff0e1710, value);
        draw.AddText(start + new Vector2(a.X, 0), Gold, value);

        const float sliderWidth = 87f;
        const float sliderHeight = 22f;
        var sliderX = origin.X + width - 112f;
        var labelX = origin.X + width - 179f;
        var fpsY = origin.Y + 44f;
        var qualityY = origin.Y + 68f;
        var textHeight = ImGui.CalcTextSize("Quality").Y;
        draw.AddText(new Vector2(labelX, fpsY + (sliderHeight - textHeight) * .5f), Muted, "FPS");
        draw.AddText(new Vector2(labelX, qualityY + (sliderHeight - textHeight) * .5f), Muted, "Quality");

        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.09f, .16f, .12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(.17f, .25f, .18f, 1f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(.91f, .76f, .43f, 1f));

        var fps = Math.Clamp(plugin.Configuration.BrowserFpsLimit, 30, 60);
        ImGui.SetCursorScreenPos(new Vector2(sliderX, fpsY));
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt("##bryergames-fps", ref fps, 30, 60))
        {
            plugin.Configuration.BrowserFpsLimit = fps;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Browser FPS target (30–60). Applies when reopening a game window.");

        var quality = Math.Clamp(plugin.Configuration.RenderScalePercent, 55, 100);
        ImGui.SetCursorScreenPos(new Vector2(sliderX, qualityY));
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt("##bryergames-quality", ref quality, 55, 100, "%d%%"))
        {
            plugin.Configuration.RenderScalePercent = quality;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Browser render quality (55–100%). Higher values look sharper but may cost more FPS. Applies when reopening a game.");
        ImGui.PopStyleColor(3);

        // Compact mode = browser mobile mode
        const float screenLabelX = 28f;
        const float screenComboX = 88f;
        const float screenComboWidth = 108f;
        var screenY = qualityY;
        draw.AddText(new Vector2(origin.X + screenLabelX,
            screenY + (sliderHeight - textHeight) * .5f), Muted, "Screen");
        ImGui.SetCursorScreenPos(new Vector2(origin.X + screenComboX, screenY));
        ImGui.SetNextItemWidth(screenComboWidth);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.09f, .16f, .12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(.17f, .25f, .18f, 1f));
        var currentMode = plugin.Configuration.ScreenMode;
        if (ImGui.BeginCombo("##bryergames-screen", currentMode == BrowserScreenMode.Compact ? "Compact" : "Desktop"))
        {
            foreach (var mode in new[] { BrowserScreenMode.Desktop, BrowserScreenMode.Compact })
            {
                var selected = mode == currentMode;
                if (ImGui.Selectable(mode.ToString(), selected) && !selected)
                {
                    plugin.Configuration.ScreenMode = mode;
                    plugin.Configuration.Save();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Desktop: current wide game window.\nCompact: portrait window using the site's mobile layout.\nApplies when opening a game.");
    }

    private void DrawGame(ImDrawListPtr draw, SiteDestination game, Vector2 position, float cellWidth, float rowHeight)
    {
        ImGui.PushID((int)game);
        var unavailable = plugin.Configuration.ScreenMode == BrowserScreenMode.Compact && Destinations.IsDesktopOnly(game);
        var featured = game is SiteDestination.Aviator or SiteDestination.BlackPrism;
        var maximumIcon = featured ? 123f : 108f;
        var iconSize = Math.Min(maximumIcon, Math.Min(cellWidth - 12f, rowHeight - (featured ? 27f : 39f)));
        var boxPosition = position + new Vector2((cellWidth - iconSize) / 2f, 1f);
        ImGui.SetCursorScreenPos(boxPosition);
        // Hoverable even when the game is unavailable but never allow the disabled selection to open a game.
        bool clicked = ImGui.InvisibleButton("##artwork", new Vector2(iconSize, iconSize));
        var artworkHovered = ImGui.IsItemHovered();
        var hovered = !unavailable && artworkHovered;
        if (!unavailable && plugin.ActiveGame == game)
        {
            // Selected game has a slow gold halo behind its art.
            var pulse = .5f + .5f * MathF.Sin((float)ImGui.GetTime() * 2.3f);
            var center = boxPosition + new Vector2(iconSize / 2f);
            var opacity = (uint)(27 + pulse * 23);
            draw.AddCircleFilled(center, iconSize * .54f, (opacity << 24) | 0x00e8b65a, 48);
            draw.AddCircle(center, iconSize * .51f, 0x99a7dff7, 48, 1.7f);
        }
        if (hovered)
        {
            draw.AddCircleFilled(boxPosition + new Vector2(iconSize / 2f), iconSize * .44f, 0x1836d6e4, 48);
            draw.AddCircle(boxPosition + new Vector2(iconSize / 2f), iconSize * .48f, 0x806ac1d8, 48, 1.1f);
        }
        var iconPosition = boxPosition + (hovered ? new Vector2(-2f, -2f) : Vector2.Zero);
        var showSize = iconSize + (hovered ? 4f : 0f);
        if (atlas != null)
        {
            var index = Destinations.IconIndex(game);
            var uv0 = new Vector2((index % 4) / 4f, (index / 4) / 2f);
            var uv1 = uv0 + new Vector2(.25f, .5f);
            draw.AddImage(atlas.Handle, iconPosition, iconPosition + new Vector2(showSize), uv0, uv1,
                unavailable ? 0x66FFFFFFu : 0xFFFFFFFFu);
        }
        else
        {
            draw.AddText(boxPosition + new Vector2(iconSize / 2f - 4f, iconSize / 2f - 9f), unavailable ? Muted : Gold, "✦");
        }
        var label = Destinations.Label(game);
        var nameSize = ImGui.CalcTextSize(label);
        var labelPos = position + new Vector2((cellWidth - nameSize.X) / 2f, rowHeight - 24f);
        draw.AddText(labelPos, unavailable ? 0xff727a70u : hovered ? Gold : Cream, label);
        // The caption is drawn directly, so it needs its own hover region.
        var mouse = ImGui.GetMousePos();
        var captionHovered = unavailable && ImGui.IsWindowHovered()
            && mouse.X >= labelPos.X - 3f && mouse.X <= labelPos.X + nameSize.X + 3f
            && mouse.Y >= labelPos.Y - 3f && mouse.Y <= labelPos.Y + nameSize.Y + 3f;
        if (unavailable && (artworkHovered || captionHovered))
            ImGui.SetTooltip("Change the \"Screen\" option to \"Desktop\" first.");
        else if (hovered) ImGui.SetTooltip("Open " + label);
        if (clicked && !unavailable)
        {
            if (!plugin.Account.IsAuthorized) RequireCode();
            else plugin.OpenGame(game);
        }
        ImGui.PopID();
    }

    private static void PaintFooter(ImDrawListPtr draw, Vector2 origin, float width, float height, float footerY)
    {
        draw.AddLine(origin + new Vector2(26, footerY), origin + new Vector2(width - 26, footerY), Border, 1f);
        draw.AddCircleFilled(origin + new Vector2(width / 2f, footerY), 3.5f, Gold, 12);
        draw.AddLine(origin + new Vector2(26, height - 13), origin + new Vector2(width - 26, height - 13), 0xff364e3b, 1f);
    }

    private void DrawAccessForm(Vector2 origin, float width, float y)
    {
        var account = plugin.Account;
        const float rowHeight = 30f;
        const float side = 20f;
        const float gap = 9f;
        const float saveWidth = 118f;
        const float offWidth = 75f;
        var labelWidth = ImGui.CalcTextSize("Access code:").X + 4f;
        var extra = account.IsAuthorized ? gap + offWidth : 0f;
        var available = Math.Max(1f, width - side * 2f);
        var inputWidth = Math.Clamp(available - labelWidth - gap * 2f - saveWidth - extra, 160f, 324f);
        var usedWidth = labelWidth + gap * 2f + inputWidth + saveWidth + extra;
        var x = origin.X + (width - usedWidth) / 2f;

        var labelY = y + (rowHeight - ImGui.CalcTextSize("Access code:").Y) * .5f;
        ImGui.GetWindowDrawList().AddText(new Vector2(x, labelY), Gold, "Access code:");
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.065f, .11f, .085f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(.12f, .18f, .13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.29f, .33f, .20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.40f, .43f, .26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.25f, .37f, .24f, 1f));

        var inputY = y + (rowHeight - ImGui.GetFrameHeight()) * .5f;
        ImGui.SetCursorScreenPos(new Vector2(x + labelWidth + gap, inputY));
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.BeginDisabled(account.IsAuthorized || account.IsBusy);
        if (account.IsAuthorized)
        {
            var appliedText = "Access code applied.";
            ImGui.InputText("##access-code", ref appliedText, 64, ImGuiInputTextFlags.ReadOnly);
        }
        else
        {
            ImGui.InputTextWithHint("##access-code", "Ask the host for one, ex: 35q24jf7", ref enteredCode, 64, ImGuiInputTextFlags.Password);
        }
        var buttonX = x + labelWidth + gap * 2f + inputWidth;
        ImGui.SetCursorScreenPos(new Vector2(buttonX, y));
        if (ImGui.Button(account.IsBusy ? "Checking..." : "Save & Apply", new Vector2(saveWidth, rowHeight)))
        {
            account.Apply(enteredCode);
            enteredCode = string.Empty;
        }
        ImGui.EndDisabled();
        if (account.IsAuthorized)
        {
            ImGui.SetCursorScreenPos(new Vector2(buttonX + saveWidth + gap, y));
            if (ImGui.Button("Log Off", new Vector2(offWidth, rowHeight)))
            {
                plugin.LogOff();
                enteredCode = string.Empty;
            }
        }
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);

        var status = account.Status;
        if (!account.IsAuthorized && !string.IsNullOrWhiteSpace(status))
        {
            var text = ImGui.CalcTextSize(status);
            ImGui.SetCursorScreenPos(new Vector2(origin.X + Math.Max(8f, (width - text.X) / 2f), y + 34f));
            ImGui.TextColored(new Vector4(.64f, .70f, .63f, 1f), status);
        }
    }

    private static void DrawAccessNotice()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size / 2f, ImGuiCond.Appearing, new Vector2(.5f, .5f));
        if (!ImGui.BeginPopupModal("Access code required##BryerGames", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("You need to apply a valid access code below first.");
        ImGui.Spacing();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(Math.Max(0, (width - 75) / 2));
        if (ImGui.Button("OK", new Vector2(75, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        atlas?.Dispose();
        if (loadingAtlas is { } pending)
            _ = pending.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
    }
}
