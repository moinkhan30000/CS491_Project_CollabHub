# Quick Start Guide - Revit Version Control

## 🎯 What We Built

A complete **Revit version control system** with:
- ✅ FastAPI backend with element-level diff engine
- ✅ C# Revit plugin with WPF UI
- ✅ Element extraction from Revit models
- ✅ Visual diff viewing with highlights
- ✅ Selective merge capabilities
- ✅ REST API for all operations

---

## ⚡ Quick Start (5 minutes)

### Step 1: Start Backend

```cmd
cd d:\CS491\CollabHub\backend
python -m pip install -r requirements.txt
python main.py
```

✅ Backend running at `http://localhost:8000`

### Step 2: Test API

Open browser: `http://localhost:8000/api/docs`

Or test with curl:
```cmd
curl http://localhost:8000/api/v1/health
```

### Step 3: Build Revit Plugin

```cmd
cd d:\CS491\CollabHub\plugins\RevitVersionControl
```

Open `RevitVersionControl.csproj` in Visual Studio, then:
1. Restore NuGet packages
2. Build solution (Ctrl+Shift+B)
3. Launch Revit 2024

✅ "Version Control" tab appears in Revit ribbon

---

## 📋 What Each File Does

### Backend Core Files

| File | Purpose |
|------|---------|
| `main.py` | FastAPI app entry point, registers routes |
| `models.py` | Pydantic models for request/response validation |
| `storage.py` | In-memory storage (use DB in production) |
| `diff_engine.py` | Computes differences between snapshots |
| `routers/auth.py` | User authentication endpoints |
| `routers/projects.py` | Project CRUD operations |
| `routers/snapshots.py` | Publish and retrieve snapshots |
| `routers/diff.py` | Diff computation endpoints |
| `routers/merge.py` | Merge and pull operations |

### Revit Plugin Files

| File | Purpose |
|------|---------|
| `Application.cs` | Add-in entry, ribbon registration |
| `Commands.cs` | Publish, Pull, Diff, History commands |
| `ApiClient.cs` | HTTP client for backend communication |
| `ElementExtractor.cs` | Extract elements from Revit model |
| `PublishDialog.xaml` | WPF dialog for publishing snapshots |
| `HistoryPane.xaml` | Dockable pane showing commit history |
| `DiffMergePane.xaml` | Pane for reviewing and applying changes |

### Schema & Documentation

| File | Purpose |
|------|---------|
| `element_snapshot_schema.json` | JSON schema for element data |
| `diff_schema.json` | JSON schema for diff results |
| `example_snapshot.json` | Sample snapshot with wall and door |
| `example_diff.json` | Sample diff showing changes |
| `API_SPEC.md` | Complete REST API documentation |
| `README.md` | Full project documentation |

---

## 🔑 Key Concepts

### Element-Level Versioning
Instead of versioning raw RVT files, we:
1. Extract element data via Revit API
2. Serialize to JSON with parameters, geometry, location
3. Store only changed elements (delta-based)
4. Reconstruct changes by applying deltas

### Diff Engine
Compares two snapshots:
- Identifies added/modified/deleted elements by UniqueId
- Tracks parameter changes (value comparisons)
- Detects geometry changes (hash-based)
- Flags conflicts for concurrent modifications

### Visual Diff
Uses Revit's `OverrideGraphicSettings`:
- Green overlay = Added elements
- Yellow overlay = Modified elements
- Red dashed = Deleted elements

### Selective Merge
Users can:
- Review changes in list view
- Check/uncheck specific elements
- Apply only selected changes
- Resolve conflicts manually

---

## 🧪 Testing Workflow

1. **Create Project** (via API or plugin settings)
2. **Publish Initial Snapshot** (Baseline commit)
3. **Make Changes in Revit** (Add walls, modify doors, etc.)
4. **Publish Second Snapshot** (Creates new commit)
5. **View Diff** (Compare two commits)
6. **Pull Changes** (Simulate multi-user scenario)
7. **Apply Selective Merge** (Choose which changes to accept)

---

## 🎨 UI Components

### Ribbon Tab: "Version Control"
- **Publish Snapshot**: Extract and upload current model
- **View History**: Show commit log in dockable pane
- **Pull Changes**: Fetch and review remote changes
- **View Diff**: Compare two versions visually
- **Settings**: Configure backend URL, credentials

### Dockable Panes
- **History Pane**: Tree view of commits with metadata
- **Diff/Merge Pane**: List of changes with checkboxes

---

## 📊 Example Data Flow

### Publishing Workflow
```
Revit Model
  ↓ (ElementExtractor.cs)
Element List (C# objects)
  ↓ (JSON serialization)
HTTP POST to /api/v1/projects/{id}/snapshots
  ↓ (Backend storage.py)
Commit created with snapshot data
  ↓
Return commit ID to plugin
```

### Diff Workflow
```
User selects base & target commits
  ↓
Plugin requests diff via API
  ↓ (Backend diff_engine.py)
Compare element lists by UniqueId
  ↓
Compute changes (added/modified/deleted)
  ↓
Return DiffResult to plugin
  ↓
Display in UI + highlight in viewport
```

---

## 🔧 Customization Points

### Backend
- Replace `storage.py` with PostgreSQL/MongoDB
- Add file storage (S3, Azure Blob) for large snapshots
- Implement WebSocket for real-time updates
- Add email notifications for conflicts

### Plugin
- Enhance visual diff with 3D comparison
- Add geometry-based diff (not just hash)
- Implement branch switching UI
- Add conflict resolution wizard

---

## ⚠️ Known Limitations

1. **In-Memory Storage**: Data lost on restart (use DB)
2. **No Password Hashing**: Simplified auth (add bcrypt)
3. **Geometry Hash**: Random UUID (implement real hash)
4. **No Pagination**: Large models may be slow
5. **Single User**: No concurrent access control yet

---

## 📚 Further Reading

- **Revit API Docs**: https://www.revitapidocs.com/
- **FastAPI Docs**: https://fastapi.tiangolo.com/
- **WPF Tutorial**: https://www.wpf-tutorial.com/
- **JWT Authentication**: https://jwt.io/

---

## 🚀 Next Steps

1. **Test locally**: Follow Quick Start above
2. **Review code**: Understand diff engine and element extraction
3. **Extend features**: Add geometry diff, branch management
4. **Production prep**: Database, security, error handling
5. **Deploy**: Cloud hosting (AWS, Azure, GCP)

---

## 💡 Architecture Decisions

### Why FastAPI?
- Modern Python framework
- Automatic API docs (Swagger/ReDoc)
- Type validation with Pydantic
- Async support for scalability

### Why C# + WPF?
- Required by Revit API (.NET only)
- WPF for rich desktop UI
- Native Windows integration

### Why Element-Level?
- Raw RVT files are proprietary binary
- No official API to diff/merge RVT
- Element extraction gives full control
- Enables selective merge

---

**Total Implementation**: ~30 files, 5000+ lines of code
**Stack**: Python/FastAPI + C#/.NET/WPF + Revit API
**Time to Run**: < 5 minutes from clone to running
