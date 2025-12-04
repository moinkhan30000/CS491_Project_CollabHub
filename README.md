# Revit Version Control (CollabHub)

Revit add-in plus Python backend for element-level version control: publish snapshots, view diffs, and selectively merge changes without touching RVT binaries.

## Stack
- Add-in: C#, .NET 8, WPF, Revit API 2026
- Backend: FastAPI (Python); persistence not yet implemented (Postgres planned)

## Layout
- `plugins/` – Revit add-in source and `.addin` manifest
- `backend/` – FastAPI server, routers, diff engine, schemas

## Quick Start
Backend:
1) `cd backend`
2) `python -m pip install -r requirements.txt`
3) `python main.py` (serves `http://localhost:8000`)

Add-in:
1) Adjust `RevitAPI`/`RevitAPIUI` paths in `plugins/RevitVersionControl/RevitVersionControl.csproj` if needed.
2) Build in Visual Studio 2022+ (net8.0, Revit API 2026).
3) Post-build copies outputs to `%AppData%\Autodesk\Revit\Addins\2026\`.

## Status
- UI mostly stubbed; backend uses in-memory storage. Real auth/persistence/merge-apply logic to be added.
