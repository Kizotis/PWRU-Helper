<div align="center">

<img src="assets/icon.png" width="140" alt="PWRU Helper icon"/>

# PWRU Helper

**A free little app to chat, read and translate Russian faster while playing on the
Perfect World RU server ([pwonline.ru](https://pwonline.ru/)).**

Made for English/French (and now Spanish/German) speakers who play on the Russian server.

</div>

---

## ✨ What it does

<img src="assets/screenshot-phrasebook.png" width="480" alt="Phrasebook"/>

**1. Phrasebook** — a big list of ready-made Russian phrases and gamer slang
(hello, gg, изи, imba, "need heal", trading, yes/no…). **Click one and it's copied** —
just paste it in the game chat with `Ctrl+V`. Every phrase shows the English meaning and
how to pronounce it. There's a search bar to find anything fast.

**2. Screen OCR** — click **"Select area & read once"**, draw a box over any Russian text
on your screen, and the app reads it and translates it (English, French, Spanish or
German), line by line. Or hit **"Start live translation"** and it keeps re-reading that
area and re-translating automatically whenever the text changes — until you press Stop.
Either way, the results appear on the **Translator** tab.

**3. Translator** — one page for **reading + writing**: type in your language and get
Russian instantly (or the other way around) at the top, and see the live screen
translations underneath.

---

## ⬇️ Download & use

Go to the **[Releases](../../releases)** page and pick whichever you prefer:

- **`PWRUHelper.exe`** — *no installation.* Download and double-click; everything is inside
  the one file. Great for putting it on a USB stick or running it anywhere.
- **`PWRUHelper-x.y.z-setup.msi`** — *classic installer.* Installs the app into
  Program Files with **Start-menu and desktop shortcuts**, and shows up in
  *Add or remove programs* for a clean uninstall. Updating just means running the newer MSI.

> Works on Windows 10 & 11. The first time you run it, Windows might warn about an
> "unknown publisher" — click **More info → Run anyway** (this is normal for small free
> apps that aren't code-signed).

### Reading Russian from the screen (one-time setup)

For the **Screen OCR** feature to read Cyrillic well, Windows needs its Russian text pack.
The app makes this easy: open the **Screen OCR** tab and click
**"Install Russian OCR (1 click)"**, then accept the Windows permission popup. Done.

*(The Phrasebook and Translator work without this — it's only for reading text off the screen.)*

---

## 💡 Tips

- Keep **"Always on top"** ticked so the window stays over your game.
- **Global shortcuts** (work while you're in the game):
  **Ctrl+Alt+P** brings the app to the front · **Ctrl+Alt+T** jumps to the translator ·
  **Ctrl+Alt+L** starts/stops live translation on your last area.
- Your settings, window position and **last live area are remembered** — use
  **"↻ Resume last area"** to restart live without re-drawing the box.
- Translating **auto-copies** the result (Ctrl+V in game). Pasting Russian flips the
  direction automatically. On a screen-read message, use **↩** to reply in Russian.
- In **live translation**, use the **OCR sensitivity** slider to tune it: lower it if the
  moving game world behind the chat makes it refresh too much; raise it to catch every
  small change.
- Your game should run in **windowed** or **borderless** mode for the screen-reading box
  to appear on top. In full-screen, alt-tab out first (or use a second monitor).
- Want to add your own phrases? Edit `phrases.json` — it's kept next to the app (portable)
  or in `%AppData%\PWRUHelper\` (when installed via the MSI). Add lines and they'll show up
  next time you open it.

---

## 💬 Made by Kizotis

This is a **free** project I made to help the community. If you'd like me to add or change
something, send me a message! Just remember it's free and made on my own time, so please
don't expect changes the same day. 🙂

- 🌐 GitHub — https://github.com/Kizotis
- 🟣 Twitch — https://www.twitch.tv/kizotis
- ▶️ YouTube — https://www.youtube.com/@kizotis
- 💬 Discord — **kizotis**

**PWRU English community Discord:** https://discord.gg/RXTZhYTJz6

---

## 🛠️ For the curious (what it's built with)

- **C# / WPF on .NET 8** — a small, native Windows app (very light on CPU/RAM, so no game lag).
- **Windows built-in OCR** (`Windows.Media.Ocr`) for reading text off the screen — free and
  on-device, no cloud, no GPU.
- **Google's free translate endpoint** for translations — no API key, no cost.
- Colors taken from the pwonline.ru theme; icon mixes my avatar with the Perfect World logo.

### Building it yourself

```powershell
# run the dev version
dotnet run

# build the single portable exe (bundles .NET + all DLLs)
./"Build Portable EXE.bat"
```

The portable build ends up in `bin/Release/.../win-x64/publish/PWRUHelper.exe`.

---

<div align="center">
<sub>Free & fan-made. Not affiliated with Perfect World or pwonline.ru.</sub>
</div>
