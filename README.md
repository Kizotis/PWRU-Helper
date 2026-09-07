<div align="center">

# PWRU Helper

**Chat, read and translate Russian while you play on Perfect World RU ([pwonline.ru](https://pwonline.ru/)).**

Free · no account · no setup · Windows 10 & 11

[![Download for Windows](https://img.shields.io/badge/⬇%20Download%20for%20Windows-2ea44f?style=for-the-badge&logo=windows&logoColor=white)](../../releases/latest)

[![Watch the demo on YouTube](https://img.youtube.com/vi/6WpfOJiD4XU/hqdefault.jpg)](https://www.youtube.com/watch?v=6WpfOJiD4XU)

*Click to watch the demo*

</div>

---

## What it does

<table>
<tr>
<td width="50%" valign="top">

### 📖 Phrasebook
Ready-made Russian phrases and gamer slang. **Click one, it's copied** — paste it in the game chat with `Ctrl+V`. Every phrase shows its meaning and how to say it.

### 👁 Screen OCR
Draw a box over the game chat. The app reads the Russian text and translates it, message by message. Turn on **live** mode and it keeps translating as new lines arrive. Chat slang is decoded under each line (`ПП = Full Moon Pavilion`).

</td>
<td width="50%" valign="top">

### ✍️ Translator
Type in your language, get Russian instantly — already copied, ready to paste. Paste Russian and it flips the other way.

### 👥 Squad builder
Tick the dungeon, class and role you want and it writes the Russian "looking for group" message for you: `в лега дд хил стук`.

</td>
</tr>
</table>

<div align="center">
<img src="assets/screenshot-phrasebook.png" width="420" alt="Phrasebook"/>&nbsp;&nbsp;
<img src="assets/screenshot-ocr.png" width="420" alt="Screen OCR"/>
</div>

---

## Download

Two files on the [Releases](../../releases) page — **take the installer if you're not sure**.

| | |
|---|---|
| **`PWRUHelper-x.y.z-setup.msi`** ✅ | Classic installer. Small download, Start-menu shortcut, clean uninstall. Starts fast from the first launch. |
| **`PWRUHelper.exe`** | No installation — one file you can run from anywhere. Big download (≈180 MB) because everything is inside. |

> **First launch after a download or an update can take up to 10 seconds with nothing on screen.** That's Windows checking a file it has never seen. Every launch after that takes about a second.

> Windows may say *"unknown publisher"* — click **More info → Run anyway**. Normal for small free apps.

**To read Russian off the screen** (one-time): open the *Screen OCR* tab → **Install Russian OCR (1 click)** → accept the Windows popup. The Phrasebook and Translator work without it.

---

## Translation

- **Free by default.** Nothing to sign up for, nothing to configure.
- **Never stops.** If a translation engine asks us to slow down, it's paused for a few minutes and the next one takes over on its own. A small coloured dot next to the Translate button tells you which engine is answering.
- **No line is translated twice.** Repeated chat comes back instantly from a small file on your PC.
- **Optional keys** (About tab) if you want better wording for what *you* write: **DeepL** or **Azure Translator**. Free stays underneath — if a key runs out, the app falls back on its own.

---

## Privacy

- **Screen reading happens on your PC.** No screenshot ever leaves your computer.
- **Only the text you translate is sent** to a translation service, exactly like using translate.google.com. If a line is private, don't translate it.
- **Saved translations stay on your PC** (`%AppData%\PWRUHelper`). About tab → **Clear cache** empties them.
- **No game memory, no injection, no automation.** It takes a picture of the area you chose and puts text on your clipboard for *you* to paste. Nothing an anti-cheat cares about.

---

## Shortcuts

Work while you're in the game.

| Keys | Does |
|---|---|
| `Ctrl+Alt+P` | Bring the app to the front |
| `Ctrl+Alt+T` | Jump to the translator |
| `Ctrl+Alt+L` | Start / stop live translation |
| `Ctrl+Alt+R` | Read the last area once (or cancel a running read) |
| `Ctrl+Alt+M` | Compact overlay on / off |
| `Enter` | Translate (`Shift+Enter` for a new line) |

---

## Tips

- **Compact overlay** — a small always-on-top window with just the live feed and a reply box. Type → Enter → Russian, copied.
- **Draw a tight box** around the last 3–4 chat lines. Smaller box, better reading.
- **Game chat takes 78 characters.** Longer translations are marked where to cut; the overlay splits them into numbered blocks.
- **Black capture in full-screen?** Play windowed / borderless, or switch *Capture method* to *Windows Graphics*.
- **Something looks off after an update?** Screen OCR tab → **↺ Reset to recommended settings**.
- **Add your own phrases or slang:** edit `phrases.json`, `slang.json` or `squad.json` next to the app (or in `%AppData%\PWRUHelper`).
- **Problem?** About tab → **📋 Copy error report** and paste it to me on Discord.

---

## Made by Kizotis

Free, made on my own time for the community. Ideas and bugs are welcome — just don't expect same-day changes. 🙂

🟣 [Twitch](https://www.twitch.tv/kizotis) · ▶️ [YouTube](https://www.youtube.com/@kizotis) · 💬 Discord **kizotis** · 🌐 [GitHub](https://github.com/Kizotis)

**PWRU English community Discord:** https://discord.gg/RXTZhYTJz6

---

<details>
<summary><b>For developers</b></summary>

C# / WPF on .NET 8, Windows built-in OCR, free public translation services (DeepL / Azure optional).

```powershell
dotnet run                          # dev version
./"Build Portable EXE.bat"          # single portable exe
./"Build MSI Installer.bat"         # MSI (installs WiX v5 the first time)
dotnet test tests/PWRUHelper.Tests  # unit tests (headless, no network)
```

Code signing, winget and hashes: [`packaging/DISTRIBUTION.md`](packaging/DISTRIBUTION.md). The diagnostic of slow first launches and translation limits: [`docs/investigations/`](docs/investigations/).

</details>

## License

[MIT](LICENSE) — free to use, modify and share.

<div align="center">
<sub>Fan-made. Not affiliated with Perfect World or pwonline.ru.</sub>
</div>
