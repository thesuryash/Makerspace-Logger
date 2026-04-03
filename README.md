# SpaceAccess.Wpf — Starter
Minimal WPF + .NET 8 access logger. Logs student check-in/out to SQLite and shows live occupancy.

## Build
1. Install .NET 8 SDK and Visual Studio 2022+ with .NET Desktop workload.
2. Open `SpaceAccess.Wpf.csproj` in Visual Studio, or run:

```powershell
dotnet restore
dotnet run -c Release
```

## Publish single-file .exe
```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```
Output: `bin\Release\net8.0-windows\win-x64\publish\SpaceAccess.Wpf.exe`

## Data
SQLite DB at `%LOCALAPPDATA%\SpaceAccess\access.sqlite`.

## Temporary Local Web Adapter (firewall-safe workaround)
If your institutional environment blocks the packaged WPF installer, you can run a local browser-based adapter that reads/writes the same SQLite DB.

### Fastest no-install option for end users
You can ship this as a **single portable `.exe`** (no .NET install required on the school machine once published):

1. On a dev machine with .NET SDK, build portable output:
   ```powershell
   ./scripts/publish-web-adapter.ps1
   ```
2. Copy `dist\LocalWebAdapter-win-x64\LocalWebAdapter.exe` to school PCs.
3. Run it directly (double-click), then open `http://127.0.0.1:5057`.

Or use:
```cmd
scripts\run-web-adapter-portable.cmd
```
which starts the exe and opens the browser.

### Dev run (if SDK is already installed)
```bash
./scripts/run-web-adapter.sh
```

or on Windows PowerShell:

```powershell
./scripts/run-web-adapter.ps1
```



### If you hit merge conflicts
If Git reports conflicts specifically in LocalWebAdapter files, run:

```powershell
./scripts/resolve-web-adapter-conflicts.ps1
```

Then commit:

```powershell
git commit -m "Resolve LocalWebAdapter merge conflicts"
```

### HTML mode (open file directly)
If you want to double-click an HTML file directly:

1. Start `LocalWebAdapter.exe` first (backend API on `http://127.0.0.1:5057`).
2. Open `LocalWebAdapter/index.html` directly in browser (`file:///...`).

The page auto-targets `http://127.0.0.1:5057` when opened from `file://` and can still read/write through the C# API to SQLite.

### Why not just a standalone HTML file?
A plain `file:///.../index.html` page cannot safely read/write your local SQLite DB directly in normal school browser policies. The local adapter exe is the thin bridge that serves HTML + API on localhost.

## Notes
- Manual entry simulates a scanner. Replace calls in `RecordEvent` with device SDK callback.
- Export CSV button writes `access_log.csv` to Desktop.
