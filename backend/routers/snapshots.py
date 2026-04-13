"""
Snapshots & Commits Router
"""

from fastapi import APIRouter, HTTPException, status, Query, Depends

from dependencies import get_current_user
from diff_engine import DiffEngine
from entities.user_entity import User
from repositories.commit_repository import CommitRepository, _RESNAPSHOT_INTERVAL
from repositories.project_repository import ProjectRepository
from repositories.user_repository import UserRepository
from schemas.commit_schema import CommitCreate, CommitDetail, CommitSummary, CommitPackageCreate
from services.operation_engine import OperationEngine

router = APIRouter()
project_repo = ProjectRepository()
commit_repo = CommitRepository()
user_repo = UserRepository()
diff_engine = DiffEngine()
operation_engine = OperationEngine()


@router.post("/{project_id}/snapshots", response_model=CommitSummary, status_code=status.HTTP_201_CREATED)
async def create_snapshot(
    project_id: str,
    commit_data: CommitCreate,
    current_user: User = Depends(get_current_user),
):
    """Publish a new snapshot (create commit)."""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    user_id = current_user.userId

    parent_commit_id = commit_data.parentCommit
    if not parent_commit_id:
        latest = commit_repo.get_latest_commit_for_model(project_id, commit_data.modelId)
        parent_commit_id = latest.commitId if latest else None

    if parent_commit_id is None:
        commit = commit_repo.create_commit(
            project_id=project_id,
            model_id=commit_data.modelId,
            message=commit_data.commitMessage,
            author=user_id,
            storage_url=None,
            parent_commit=None,
            snapshot=commit_data.snapshot,
            diff=None,
            ops_payload=None,
            element_count=len(commit_data.snapshot.elements),
            changed_elements=len(commit_data.snapshot.elements),
        )
        return _build_summary(commit, user_id, user_repo, commit_data)

    parent_snapshot = commit_repo.get_snapshot(parent_commit_id)
    if not parent_snapshot:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Parent commit {parent_commit_id} snapshot could not be reconstructed.",
        )

    diff_result = diff_engine.compute_diff(
        base_elements=parent_snapshot.elements,
        target_elements=commit_data.snapshot.elements,
        base_version=parent_commit_id,
        target_version="new",
    )

    if len(diff_result.changes) == 0:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail="No changes detected. Snapshot is already up to date.",
        )

    delta_depth = commit_repo.count_delta_depth(parent_commit_id)
    is_resnapshot = delta_depth >= _RESNAPSHOT_INTERVAL
    ops_payload = operation_engine.build_payload_from_changes(diff_result.changes)

    commit = commit_repo.create_commit(
        project_id=project_id,
        model_id=commit_data.modelId,
        message=commit_data.commitMessage,
        author=user_id,
        storage_url=None,
        parent_commit=parent_commit_id,
        snapshot=commit_data.snapshot if is_resnapshot else None,
        diff=None,
        ops_payload=ops_payload if not is_resnapshot else None,
        element_count=len(commit_data.snapshot.elements),
        changed_elements=len(diff_result.changes),
    )

    return _build_summary(commit, user_id, user_repo, commit_data)


@router.post("/{project_id}/packages", response_model=CommitSummary, status_code=status.HTTP_201_CREATED)
async def create_commit_package(
    project_id: str,
    package_data: CommitPackageCreate,
    current_user: User = Depends(get_current_user),
):
    """Publish a compact change package without uploading a full snapshot."""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    user_id = current_user.userId

    if not package_data.parentCommit:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Change package publish requires parentCommit.",
        )

    parent_commit = commit_repo.get_commit(package_data.parentCommit)
    if not parent_commit or parent_commit.projectId != project_id:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Parent commit {package_data.parentCommit} not found.",
        )

    if len(package_data.changes) == 0:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail="No changes detected. Package is already up to date.",
        )

    delta_depth = commit_repo.count_delta_depth(package_data.parentCommit)
    if delta_depth >= _RESNAPSHOT_INTERVAL:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail="Checkpoint required. Publish a full snapshot for this commit.",
        )

    ops_payload = operation_engine.build_payload_from_changes(package_data.changes)
    commit = commit_repo.create_commit(
        project_id=project_id,
        model_id=package_data.modelId,
        message=package_data.commitMessage,
        author=user_id,
        storage_url=None,
        parent_commit=package_data.parentCommit,
        snapshot=None,
        diff=None,
        ops_payload=ops_payload,
        element_count=package_data.elementCount,
        changed_elements=len(package_data.changes),
    )

    return _build_summary(
        commit,
        user_id,
        user_repo,
        author_name=current_user.fullName,
    )

def _build_summary(commit, user_id: str, user_repo, commit_data=None, author_name: str = None) -> CommitSummary:
    author_info = user_repo.get_user_by_id(user_id)
    author_payload = {
        "userId": user_id,
        "fullName": author_info.fullName if author_info else (
            author_name
            or getattr(getattr(commit_data, "snapshot", None), "userName", None)
            or "Unknown"
        ),
    }
    commit_dict = commit.model_dump(exclude={"snapshot"})
    commit_dict["author"] = author_payload
    return CommitSummary(**commit_dict)


@router.get("/{project_id}/commits", response_model=dict)
async def list_commits(
    project_id: str,
    limit: int = Query(50, ge=1, le=100),
    offset: int = Query(0, ge=0),
    current_user: User = Depends(get_current_user),
):
    """Get commit history for a project."""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    commits = commit_repo.list_commits(project_id, limit, offset)
    total = commit_repo.get_commit_count(project_id)

    enriched_commits = []
    for commit in commits:
        commit_dict = commit.model_dump(exclude={"snapshot"})
        user = user_repo.get_user_by_id(commit.author)
        commit_dict["author"] = {
            "userId": commit.author,
            "fullName": user.fullName if user else "Unknown",
        }
        enriched_commits.append(commit_dict)

    return {"commits": enriched_commits, "total": total, "limit": limit, "offset": offset}


@router.get("/{project_id}/commits/{commit_id}", response_model=CommitDetail)
async def get_commit(
    project_id: str,
    commit_id: str,
    current_user: User = Depends(get_current_user),
):
    """Get specific commit details."""
    commit = commit_repo.get_commit(commit_id)
    if not commit or commit.projectId != project_id:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Commit not found")

    return CommitDetail(
        **commit.model_dump(exclude={"snapshot"}),
        children=[],
        summary={"added": 0, "modified": 0, "deleted": 0},
    )


@router.get("/{project_id}/commits/{commit_id}/snapshot")
async def get_snapshot(
    project_id: str,
    commit_id: str,
    current_user: User = Depends(get_current_user),
):
    """Reconstruct and return the full snapshot for any commit."""
    commit = commit_repo.get_commit(commit_id)
    if not commit or commit.projectId != project_id:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Commit not found")

    snapshot = commit_repo.get_snapshot(commit_id)
    if not snapshot:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Snapshot not found")

    return snapshot
