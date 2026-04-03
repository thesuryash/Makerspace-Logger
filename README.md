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

### What it does
- Hosts a local web app at `http://127.0.0.1:5057`.
- Uses the same `Users`, `Locations`, and `Events` tables used by the desktop app.
- Supports entry/exit/manual event recording and occupancy summary.

### Run it
From repo root:

```bash
./scripts/run-web-adapter.sh
```

or on Windows PowerShell:

```powershell
./scripts/run-web-adapter.ps1
```

Then open `http://127.0.0.1:5057` in any browser already allowed by school policy.

> Tip: Launch the WPF app once first so the DB gets created.

## Notes
- Manual entry simulates a scanner. Replace calls in `RecordEvent` with device SDK callback.
- Export CSV button writes `access_log.csv` to Desktop.
