"""
Snapshots & Commits Router
"""

from fastapi import APIRouter, HTTPException, status, Query, Depends
from schemas.commit_schema import CommitCreate, CommitDetail, CommitSummary
from schemas.element_schema import ElementSnapshot
from entities.user_entity import User
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository
from repositories.user_repository import UserRepository
from dependencies import get_current_user
from diff_engine import DiffEngine

router = APIRouter()
project_repo = ProjectRepository()
commit_repo = CommitRepository()
user_repo = UserRepository()
diff_engine = DiffEngine()

@router.post("/{project_id}/snapshots", response_model=CommitSummary, status_code=status.HTTP_201_CREATED)
async def create_snapshot(
    project_id: str,
    commit_data: CommitCreate,
    current_user: User = Depends(get_current_user)
):
    """Publish a new snapshot (create commit)"""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )

    user_id = current_user.userId

    parent_commit_id = commit_data.parentCommit
    if not parent_commit_id:
        latest_commit = commit_repo.get_latest_commit_for_model(project_id, commit_data.modelId)
        parent_commit_id = latest_commit.commitId if latest_commit else None

    changed_elements = None
    if parent_commit_id:
        parent_snapshot = commit_repo.get_snapshot(parent_commit_id)
        if parent_snapshot:
            diff_result = diff_engine.compute_diff(
                base_elements=parent_snapshot.elements,
                target_elements=commit_data.snapshot.elements,
                base_version=parent_commit_id,
                target_version="new"
            )
            changed_elements = len(diff_result.changes)
            if changed_elements == 0:
                raise HTTPException(
                    status_code=status.HTTP_409_CONFLICT,
                    detail="No changes detected. Snapshot is already up to date."
                )

    commit = commit_repo.create_commit(
        project_id=project_id,
        model_id=commit_data.modelId,
        message=commit_data.commitMessage,
        author=user_id,
        storage_url=None,
        parent_commit=parent_commit_id,
        snapshot=commit_data.snapshot,
        element_count=len(commit_data.snapshot.elements),
        changed_elements=changed_elements
    )

    author_info = user_repo.get_user_by_id(user_id)
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
    offset: int = Query(0, ge=0),
    current_user: User = Depends(get_current_user)
):
    """Get commit history for a project"""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )

    commits = commit_repo.list_commits(project_id, limit, offset)
    total = commit_repo.get_commit_count(project_id)

    enriched_commits = []
    for commit in commits:
        commit_dict = commit.model_dump(exclude={"snapshot"})
        user = user_repo.get_user_by_id(commit.author)
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
async def get_commit(
    project_id: str,
    commit_id: str,
    current_user: User = Depends(get_current_user)
):
    """Get specific commit details"""
    commit = commit_repo.get_commit(commit_id)
    if not commit or commit.projectId != project_id:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Commit not found"
        )

    summary = {
        "added": 0,
        "modified": 0,
        "deleted": 0
    }

    return CommitDetail(
        **commit.model_dump(exclude={"snapshot"}),
        children=[],
        summary=summary
    )

@router.get("/{project_id}/commits/{commit_id}/snapshot", response_model=ElementSnapshot)
async def get_snapshot(
    project_id: str,
    commit_id: str,
    current_user: User = Depends(get_current_user)
):
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
