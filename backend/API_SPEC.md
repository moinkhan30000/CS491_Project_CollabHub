# Revit Version Control API Specification

## Overview
FastAPI-based REST API for managing Revit element snapshots, computing diffs, and handling version control operations.

**Base URL:** `http://localhost:8000/api/v1`

---

## Authentication

All endpoints require authentication via JWT token in the Authorization header:
```
Authorization: Bearer <token>
```

### POST /auth/register
Register a new user.

**Request:**
```json
{
  "email": "user@example.com",
  "password": "securepassword",
  "fullName": "John Doe"
}
```

**Response:** `201 Created`
```json
{
  "userId": "user-uuid",
  "email": "user@example.com",
  "fullName": "John Doe",
  "createdAt": "2025-12-03T10:00:00Z"
}
```

### POST /auth/login
Authenticate and receive JWT token.

**Request:**
```json
{
  "email": "user@example.com",
  "password": "securepassword"
}
```

**Response:** `200 OK`
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "refresh-token-uuid",
  "expiresIn": 3600,
  "user": {
    "userId": "user-uuid",
    "email": "user@example.com",
    "fullName": "John Doe"
  }
}
```

---

## Projects

### GET /projects
List all projects accessible to the user.

**Response:** `200 OK`
```json
{
  "projects": [
    {
      "projectId": "project-12345",
      "name": "Office Building",
      "description": "Main office renovation",
      "createdBy": "user-uuid",
      "createdAt": "2025-11-01T10:00:00Z",
      "lastModified": "2025-12-03T10:00:00Z",
      "memberCount": 5
    }
  ]
}
```

### POST /projects
Create a new project.

**Request:**
```json
{
  "name": "Office Building",
  "description": "Main office renovation",
  "settings": {
    "autoSaveInterval": 300,
    "maxSnapshotSize": 104857600
  }
}
```

**Response:** `201 Created`
```json
{
  "projectId": "project-12345",
  "name": "Office Building",
  "description": "Main office renovation",
  "createdBy": "user-uuid",
  "createdAt": "2025-12-03T10:00:00Z"
}
```

### GET /projects/{projectId}
Get project details.

**Response:** `200 OK`
```json
{
  "projectId": "project-12345",
  "name": "Office Building",
  "description": "Main office renovation",
  "settings": {
    "autoSaveInterval": 300,
    "maxSnapshotSize": 104857600
  },
  "members": [
    {
      "userId": "user-uuid",
      "role": "owner",
      "joinedAt": "2025-11-01T10:00:00Z"
    }
  ],
  "statistics": {
    "totalCommits": 45,
    "totalElements": 1250,
    "storageUsed": 52428800
  }
}
```

---

## Snapshots & Commits

### POST /projects/{projectId}/snapshots
Publish a new snapshot (commit).

**Request:** (multipart/form-data or JSON)
```json
{
  "modelId": "model-abc-v1",
  "commitMessage": "Added walls for conference room",
  "parentCommit": "commit-abc123",
  "snapshot": {
    "version": "1.0",
    "projectId": "project-12345",
    "modelId": "model-abc-v1",
    "timestamp": "2025-12-03T10:30:00Z",
    "userName": "john.doe@example.com",
    "elements": [...]
  }
}
```

**Response:** `201 Created`
```json
{
  "commitId": "commit-def456",
  "projectId": "project-12345",
  "modelId": "model-abc-v1",
  "message": "Added walls for conference room",
  "author": "user-uuid",
  "timestamp": "2025-12-03T10:30:00Z",
  "parentCommit": "commit-abc123",
  "elementCount": 125,
  "changedElements": 12
}
```

### GET /projects/{projectId}/commits
Get commit history for a project.

**Query Parameters:**
- `limit` (default: 50)
- `offset` (default: 0)
- `branch` (optional)
- `author` (optional)

**Response:** `200 OK`
```json
{
  "commits": [
    {
      "commitId": "commit-def456",
      "message": "Added walls for conference room",
      "author": {
        "userId": "user-uuid",
        "fullName": "John Doe"
      },
      "timestamp": "2025-12-03T10:30:00Z",
      "parentCommit": "commit-abc123",
      "elementCount": 125,
      "changedElements": 12
    }
  ],
  "total": 45,
  "limit": 50,
  "offset": 0
}
```

### GET /projects/{projectId}/commits/{commitId}
Get specific commit details.

**Response:** `200 OK`
```json
{
  "commitId": "commit-def456",
  "projectId": "project-12345",
  "modelId": "model-abc-v1",
  "message": "Added walls for conference room",
  "author": {
    "userId": "user-uuid",
    "fullName": "John Doe",
    "email": "john.doe@example.com"
  },
  "timestamp": "2025-12-03T10:30:00Z",
  "parentCommit": "commit-abc123",
  "children": ["commit-ghi789"],
  "elementCount": 125,
  "changedElements": 12,
  "summary": {
    "added": 5,
    "modified": 7,
    "deleted": 0
  }
}
```

### GET /projects/{projectId}/commits/{commitId}/snapshot
Download full snapshot for a commit.

**Response:** `200 OK` (JSON following element_snapshot_schema.json)

---

## Diff Operations

### GET /projects/{projectId}/diff
Compare two commits and get differences.

**Query Parameters:**
- `base` (commitId) - Base commit
- `target` (commitId) - Target commit to compare

**Response:** `200 OK` (JSON following diff_schema.json)
```json
{
  "baseVersion": "commit-abc123",
  "targetVersion": "commit-def456",
  "timestamp": "2025-12-03T11:00:00Z",
  "summary": {
    "added": 5,
    "modified": 7,
    "deleted": 0,
    "total": 12
  },
  "changes": [...]
}
```

### POST /projects/{projectId}/diff/analyze
Analyze potential conflicts between local changes and remote commits.

**Request:**
```json
{
  "baseCommit": "commit-abc123",
  "localChanges": {
    "elements": [...]
  },
  "targetCommit": "commit-def456"
}
```

**Response:** `200 OK`
```json
{
  "hasConflicts": true,
  "conflicts": [
    {
      "elementId": "1a2b3c4d-5e6f-7g8h-9i0j-k1l2m3n4o5p6",
      "conflictType": "concurrent_modification",
      "description": "Wall height modified in both local and remote",
      "localChange": {...},
      "remoteChange": {...},
      "resolutionOptions": ["keep_local", "accept_remote", "manual_resolve"]
    }
  ],
  "safeChanges": [...]
}
```

---

## Merge Operations

### POST /projects/{projectId}/merge
Request a merge operation (3-way merge if possible).

**Request:**
```json
{
  "baseCommit": "commit-abc123",
  "sourceCommit": "commit-def456",
  "targetCommit": "commit-ghi789",
  "resolutions": [
    {
      "elementId": "1a2b3c4d-5e6f-7g8h-9i0j-k1l2m3n4o5p6",
      "resolution": "accept_remote",
      "customData": null
    }
  ],
  "message": "Merged remote changes with conflict resolution"
}
```

**Response:** `200 OK`
```json
{
  "mergeCommitId": "commit-jkl012",
  "status": "success",
  "appliedChanges": 12,
  "skippedChanges": 0,
  "conflicts": []
}
```

### POST /projects/{projectId}/pull
Pull changes from a specific commit (simplified merge).

**Request:**
```json
{
  "currentCommit": "commit-abc123",
  "targetCommit": "commit-def456",
  "strategy": "auto",
  "selectiveElements": ["element-id-1", "element-id-2"]
}
```

**Response:** `200 OK`
```json
{
  "changes": [...],
  "conflicts": [],
  "requiresResolution": false
}
```

---

## Branches (Optional for MVP)

### GET /projects/{projectId}/branches
List all branches.

**Response:** `200 OK`
```json
{
  "branches": [
    {
      "branchId": "branch-main",
      "name": "main",
      "headCommit": "commit-def456",
      "createdBy": "user-uuid",
      "createdAt": "2025-11-01T10:00:00Z",
      "isDefault": true
    }
  ]
}
```

### POST /projects/{projectId}/branches
Create a new branch.

**Request:**
```json
{
  "name": "feature-new-layout",
  "fromCommit": "commit-def456"
}
```

**Response:** `201 Created`

---

## Health & Status

### GET /health
Check API health status.

**Response:** `200 OK`
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "timestamp": "2025-12-03T10:00:00Z",
  "database": "connected",
  "storage": "available"
}
```

---

## Error Responses

All errors follow this format:

```json
{
  "error": {
    "code": "CONFLICT_ERROR",
    "message": "Merge conflicts detected",
    "details": {
      "conflictCount": 3,
      "elementIds": ["id1", "id2", "id3"]
    },
    "timestamp": "2025-12-03T10:00:00Z"
  }
}
```

**Common HTTP Status Codes:**
- `400 Bad Request` - Invalid request data
- `401 Unauthorized` - Missing or invalid auth token
- `403 Forbidden` - Insufficient permissions
- `404 Not Found` - Resource not found
- `409 Conflict` - Merge conflicts or concurrent modification
- `422 Unprocessable Entity` - Validation errors
- `500 Internal Server Error` - Server error

---

## Rate Limiting

- 100 requests per minute per user
- 10 snapshot uploads per minute per project
- Headers returned: `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`
