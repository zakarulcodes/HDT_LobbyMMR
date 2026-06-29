# HDT_LobbyMMR

A [Hearthstone Deck Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker)
plugin that shows the **MMR of every player in your Battlegrounds lobby**, in a
panel **skinned to match HDT's Battlegrounds Session window** and **docked to the
top** of it.

It combines the leaderboard lookup approach of
[IBM5100's HDT_BGrank](https://github.com/IBM5100o/HDT_BGrank) with the visual
style and placement of HDT's own Battlegrounds Session overlay.

![reference look](docs/reference.png)

## How it works

- **Skin** — uses the exact colors from HDT's `BattlegroundsSession.xaml`
  (body `#2E3235`, border `#4A5256`, header `#1C2022`, 240px wide) so it blends
  in with the native session panel.
- **Docking** — at runtime it finds HDT's `BattlegroundsSessionStackPanel` on the
  overlay and inserts itself as the top child, so it is physically attached to the
  top of the Battlegrounds Session window and follows it automatically. It scales
  with the session window (mirroring `Config.OverlaySessionRecapScaling`), using a
  bottom-left transform origin so it stays flush and horizontally aligned at any
  scale.
- **Data** — on entering a BG match it downloads the regional leaderboard from
  `https://bgrank.fly.dev` (cached locally as a fallback), reads the lobby player
  names from Hearthstone memory via UnitySpy, and shows each player's MMR sorted
  highest-first. **You** are highlighted in gold.
  - Players above 8000 MMR show their exact rating.
  - Players the leaderboard can't resolve show `8000↓` (or `-` on the CN region).
  - Names only become readable **after you mouse over the leaderboard tiles**
    in-game — this is the same limitation HDT_BGrank has.

## Build

Requires the .NET SDK (the build pulls the .NET Framework 4.7.2 reference
assemblies automatically — no Visual Studio needed).

```
dotnet build HDT_LobbyMMR/HDT_LobbyMMR.csproj -c Release
```

Output: `HDT_LobbyMMR/bin/Release/HDT_LobbyMMR.dll`.

The project references `HearthstoneDeckTracker.exe` and `untapped-scry-dotnet.dll`
from `HDT_LobbyMMR/Reference/` (copied from your HDT install) for **compilation
only** — neither is shipped, because HDT already loads both at runtime.

## Install

1. Close HDT (Windows locks loaded plugin DLLs).
2. Run `powershell -File HDT_LobbyMMR/deploy.ps1`, **or** manually copy
   `HDT_LobbyMMR.dll` into
   `%AppData%\HearthstoneDeckTracker\Plugins\HDT_LobbyMMR\`.
3. Start HDT → **Options → Tracker → Plugins** → enable **Lobby MMR**.
4. The **Reset** button re-creates the panel if the overlay was rebuilt.

Logs: `%AppData%\HearthstoneDeckTracker\LobbyMMR\LobbyMMR-<date>.log`.

## Updating the HDT reference after an HDT update

If a future HDT update changes the API, refresh the bundled reference assemblies:

```
copy "%LocalAppData%\HearthstoneDeckTracker\app-<version>\HearthstoneDeckTracker.exe"   HDT_LobbyMMR\Reference\
copy "%LocalAppData%\HearthstoneDeckTracker\app-<version>\untapped-scry-dotnet.dll"     HDT_LobbyMMR\Reference\
```

The Unity version string in `Mirror.cs` (`2021.3.25.61228`) may also need bumping
if Blizzard upgrades Hearthstone's Unity engine.

## Credit

Leaderboard service and memory-reading approach by **IBM5100**
([HDT_BGrank](https://github.com/IBM5100o/HDT_BGrank),
[BGrank_bot](https://github.com/IBM5100o/BGrank_bot)).
