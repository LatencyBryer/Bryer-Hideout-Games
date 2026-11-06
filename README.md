<h1 align="center">
  <strong>[W.I.P] *Not recommended to use yet</strong>
</h1>

<p align="center">
  <img src="icon.png" alt="Bryer's Hideout Games" width="128" />
</p>

<h1 align="center">🎮 Bryer's Hideout Games</h1>

<p align="center">
  <strong>Your Bryer's Hideout minigames, right inside Final Fantasy.</strong>
</p>

<p align="center">
  <img alt="Dalamud API" src="https://img.shields.io/badge/Dalamud-API%2015-green" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-purple" />
  <img alt="Platform" src="https://img.shields.io/badge/Windows-x64-blue" />
</p>

---

## 📥 Installation

### Dalamud custom repository

1. Open FFXIV through XIVLauncher with Dalamud enabled.
2. Type `/xlsettings` and open **Experimental → Custom Plugin Repositories**.
3. Add the following address and enable it:

   ```text
   https://plugins.bybryer.com/repo.json
   ```

4. Open `/xlplugins`, search for **Bryer's Hideout Games**, and install it.
5. Type `/bryergames` to open the launcher.

---

## ✨ What I made

I built **Bryer's Hideout Games** to bring the minigames from [Bryer's Hideout](https://minigames.bybryer.com) into FFXIV without sending you back and forth between the game and your desktop browser.

Type `/bryergames`, choose a game, and play in a movable, resizable window.

**Features**

- 🕹️ **Seven minigames**, launched directly from in-game.
- 🔐 **Access-code linking**, validated against Bryer's Hideout and stored.
- 💰 **Your balance** displayed in the launcher.
- 📱 **Screen modes:** Desktop and Compact (some games require Desktop).
- 🎚️ **Performance settings:** 30–60 FPS, adjustable render quality.

> [!IMPORTANT]
> This plugin is not affiliated with Square Enix, XIVLauncher, or the Dalamud project. It requires a valid Bryer's Hideout access code issued through your Venue Host.

## 🎲 Minigames

| Game | Command | Desktop | Compact |
|:--|:--|:--:|:--:|
| Mines | `/mines` | ✅ | ✅ |
| Plinko | `/plinko` | ✅ | ❌ |
| Fortune Wheel | `/wheel` | ✅ | ✅ |
| Aviator (Boosted+) | `/aviator` | ✅ | ✅ |
| Nalah, Go Eat! | `/nalah` | ✅ | ✅ |
| Black Prism | `/blackprism` | ✅ | ❌ |
| Whispering Library | `/whisperinglibrary` | ✅ | ❌ |

The launcher is always available through **`/bryergames`**. Compact is the default screen mode. Games unavailable in Compact appear dimmed and tell you to switch to Desktop on hover.

## 🔑 Access code & account

1. Ask your Venue Host for your **access code**.
2. Enter it in the launcher and select **Save & Apply**.
3. After verification, choose a game or use one of its slash commands.
4. Select **Log Off** to clear the local link and browser profile when changing accounts.

I encrypt the saved code/token with Windows DPAPI for your Windows user.

> [!CAUTION]
> Keep your access code private.

## ⚙️ Controls

| Setting | Purpose | Default |
|---|---|---|
| Screen | Desktop or mobile-style Compact window | Compact |
| FPS | Browser render frame cap | 60 |
| Quality | Internal rendering resolution | 100% |

<details>
<summary><strong>🧩 Why is Chromium an external process?</strong></summary>

Dalamud loads plugins in a collectible .NET assembly context. CefSharp's native/mixed-mode runtime cannot be loaded safely in that context. The helper process isolates Chromium, communicates through local pipes, and sends rendered frames to the Dalamud UI. It is a runtime dependency **built from this source**.

</details>

<details>
<summary><strong>🛠️ Troubleshooting</strong></summary>

- **Chromium host missing:** Open your plugin folder, run the file `build-devplugin.ps1`.
- **Blank game or IPC error:** check `/xllog` and `ChromiumProfile/Games/<profile>/browser-helper.log` under the Dalamud plugin config directory. Do not post private URLs or codes.
- **Slow performance:** lower Quality or FPS, then reopen the game window.
- **A game is dimmed:** switch Screen to **Desktop**.

</details>

## 💬 About & links

Made by **Latency Bryer** for Bryer's Hideout.

- 🌐 [Website](https://lootbox.bybryer.com)
- 🎮 [Minigames](https://minigames.bybryer.com)
- 🧩 [Source code and releases](https://github.com/LatencyBryer/Bryer-Hideout-Games)
- 📝 [Potfolio](https://bybryer.com)

**© 2026 Latency Bryer — All Rights Reserved. Consult http://lootbox.bybryer.com/ Copyright popup for full claims.**
