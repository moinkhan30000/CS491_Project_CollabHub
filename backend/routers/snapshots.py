"""
Snapshots & Commits Router
"""

from fastapi import APIRouter, HTTPException, status, Query
from typing import Optional
from models import CommitCreate, Commit, CommitDetail, ElementSnapshot, CommitSummary
from storage import storage
from diff_engine import DiffEngine

router = APIRouter()
diff_engine = DiffEngine()

@router.post("/{project_id}/snapshots", response_model=CommitSummary, status_code=status.HTTP_201_CREATED)
async def create_snapshot(project_id: str, commit_data: CommitCreate):
    """Publish a new snapshot (create commit)"""
    
    # Verify project exists
    project = storage.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )
    
    # In production, get user_id from JWT
    user_id = "default-user"

    # Determine parent commit if not provided
    parent_commit_id = commit_data.parentCommit
    if not parent_commit_id:
        latest_commit = storage.get_latest_commit(project_id, commit_data.modelId)
        parent_commit_id = latest_commit.commitId if latest_commit else None

    # Compute diff vs parent (if exists) for change counts
    changed_elements = None
    if parent_commit_id:
        parent_snapshot = storage.get_snapshot(parent_commit_id)
        if parent_snapshot:
            diff_result = diff_engine.compute_diff(
                base_elements=parent_snapshot.elements,
                target_elements=commit_data.snapshot.elements,
                base_version=parent_commit_id,
                target_version="new"
            )
            changed_elements = len(diff_result.changes)
    
    # Create commit with snapshot
    commit = storage.create_commit(
        project_id=project_id,
        model_id=commit_data.modelId,
        message=commit_data.commitMessage,
        author=user_id,
        snapshot=commit_data.snapshot,
        parent_commit=parent_commit_id,
        changed_elements=changed_elements
    )
    author_info = storage.get_user_by_id(user_id)
    author_payload = {
        "userId": user_id,
        "fullName": author_info.fullName if author_info else (commit_data.snapshot.userName or "Unknown")
    }

    commit_dict = commit.model_dump(exclude={"snapshot"})
    commit_dict["author"] = author_payload

    return CommitSummary(**commit_dict)

@router.get("/{project_id}/commits", response_model=dict)
async def list_commits(
    project_id: str,
    limit: int = Query(50, ge=1, le=100),
    offset: int = Query(0, ge=0)
):
    """Get commit history for a project"""
    
    # Verify project exists
    project = storage.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )
    
    commits = storage.list_commits(project_id, limit, offset)
    total = storage.get_commit_count(project_id)
    
    # Enrich commits with author info
    enriched_commits = []
    for commit in commits:
        # CRITICAL FIX: Exclude the 'snapshot' field
        # The snapshot is huge (MBs of data). We don't want to send it 
        # when just listing the history.
        commit_dict = commit.model_dump(exclude={"snapshot"})
        user = storage.get_user_by_id(commit.author)
        commit_dict["author"] = {
            "userId": commit.author,
            "fullName": user.fullName if user else "Unknown"
        }
        enriched_commits.append(commit_dict)
    
    return {
        "commits": enriched_commits,
        "total": total,
        "limit": limit,
        "offset": offset
    }

@router.get("/{project_id}/commits/{commit_id}", response_model=CommitDetail)
async def get_commit(project_id: str, commit_id: str):
    """Get specific commit details"""
    
    commit = storage.get_commit(commit_id)
    if not commit or commit.projectId != project_id:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Commit not found"
        )
    
    # Get snapshot to compute summary
    snapshot = storage.get_snapshot(commit_id)
    
    summary = {
        "added": 0,
        "modified": 0,
        "deleted": 0
    }

    # Convert DB Object -> Dictionary, removing the heavy snapshot
    commit_data = commit.model_dump(exclude={"snapshot"})
    
    # In production, compute real summary by comparing with parent
    
    return CommitDetail(
        **commit.model_dump(),
        children=[],
        summary=summary
    )

@router.get("/{project_id}/commits/{commit_id}/snapshot", response_model=ElementSnapshot)
async def get_snapshot(project_id: str, commit_id: str):
    """Download full snapshot for a commit"""
    
    commit = storage.get_commit(commit_id)
    if not commit or commit.projectId != project_id:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Commit not found"
        )
    
    snapshot = storage.get_snapshot(commit_id)
    if not snapshot:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Snapshot not found"
        )
    
    return snapshot
