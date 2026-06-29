# Reference assemblies

These DLLs are **not committed** to the repo (they ship with Hearthstone Deck
Tracker and aren't ours to redistribute). Copy them here before building:

- `HearthstoneDeckTracker.exe`
- `untapped-scry-dotnet.dll`

Both live in your HDT install:

```
%LocalAppData%\HearthstoneDeckTracker\app-<version>\
```

The project references them for **compilation only** (`Private=False`) — they are
not shipped with the plugin, because HDT already loads both at runtime.
