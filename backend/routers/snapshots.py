"""
Snapshots & Commits Router
"""

from fastapi import APIRouter, HTTPException, status, Query
from typing import Optional
from models import CommitCreate, CommitDetail, ElementSnapshot
from entities.commit_entity import Commit
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository
from repositories.user_repository import UserRepository

router = APIRouter()
project_repo = ProjectRepository()
commit_repo = CommitRepository()
user_repo = UserRepository()

@router.post("/{project_id}/snapshots", response_model=Commit, status_code=status.HTTP_201_CREATED)
async def create_snapshot(project_id: str, commit_data: CommitCreate):
    """Publish a new snapshot (create commit)"""
    
    # Verify project exists
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )
    
    # In production, get user_id from JWT
    user_id = "default-user"
    
    # Create commit with snapshot
    commit = commit_repo.create_commit(
        project_id=project_id,
        model_id=commit_data.modelId,
        message=commit_data.commitMessage,
        author=user_id,
        snapshot=commit_data.snapshot,
        parent_commit=commit_data.parentCommit
    )
    
    return commit

@router.get("/{project_id}/commits", response_model=dict)
async def list_commits(
    project_id: str,
    limit: int = Query(50, ge=1, le=100),
    offset: int = Query(0, ge=0)
):
    """Get commit history for a project"""
    
    # Verify project exists
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )
    
    commits = commit_repo.list_commits(project_id, limit, offset)
    total = commit_repo.get_commit_count(project_id)
    
    # Enrich commits with author info
    enriched_commits = []
    for commit in commits:
        # CRITICAL FIX: Exclude the 'snapshot' field
        # The snapshot is huge (MBs of data). We don't want to send it 
        # when just listing the history.
        commit_dict = commit.model_dump(exclude={"snapshot"})
        user = user_repo.get_user_by_id(commit.author)
        if user:
            commit_dict["author"] = {
                "userId": user.userId,
                "fullName": user.fullName
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
    
    commit = commit_repo.get_commit(commit_id)
    if not commit or commit.projectId != project_id:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Commit not found"
        )
    
    # Get snapshot to compute summary
    snapshot = commit_repo.get_snapshot(commit_id)
    
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
    
    commit = commit_repo.get_commit(commit_id)
    if not commit or commit.projectId != project_id:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Commit not found"
        )
    
    snapshot = commit_repo.get_snapshot(commit_id)
    if not snapshot:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Snapshot not found"
        )
    
    return snapshot
