"""
Diff Router
"""

from fastapi import APIRouter, HTTPException, status, Query, Depends
from schemas.diff_schema import DiffResult
from entities.user_entity import User
from dependencies import get_current_user
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository
from diff_engine import DiffEngine

router = APIRouter()
diff_engine = DiffEngine()
project_repo = ProjectRepository()
commit_repo = CommitRepository()

@router.get("/{project_id}/diff", response_model=DiffResult)
async def compute_diff(
    project_id: str,
    base: str = Query(..., description="Base commit ID"),
    target: str = Query(..., description="Target commit ID"),
    current_user: User = Depends(get_current_user)
):
    """Compare two commits and get differences"""

    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )

    # Get raw commits first to check they exist
    from repositories.commit_repository import CommitRepository
    from sqlmodel import Session
    from database import engine
    from entities.commit_entity import Commit as CommitEntity

    with Session(engine) as session:
        base_commit_raw = session.get(CommitEntity, base)
        target_commit_raw = session.get(CommitEntity, target)

    if not base_commit_raw:
        raise HTTPException(status_code=404, detail=f"Base commit {base} not found in DB")

    if not target_commit_raw:
        raise HTTPException(status_code=404, detail=f"Target commit {target} not found in DB")

    if not base_commit_raw.snapshot:
        raise HTTPException(status_code=404, detail=f"Base commit {base} has no snapshot data. Was it published with element extraction?")

    if not target_commit_raw.snapshot:
        raise HTTPException(status_code=404, detail=f"Target commit {target} has no snapshot data. Was it published with element extraction?")

    # Now try to deserialize
    try:
        base_snapshot = commit_repo.get_snapshot(base)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to deserialize base snapshot: {str(e)}")

    try:
        target_snapshot = commit_repo.get_snapshot(target)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to deserialize target snapshot: {str(e)}")

    if not base_snapshot:
        raise HTTPException(status_code=500, detail=f"Base snapshot deserialization returned None unexpectedly")

    if not target_snapshot:
        raise HTTPException(status_code=500, detail=f"Target snapshot deserialization returned None unexpectedly")

    diff_result = diff_engine.compute_diff(
        base_elements=base_snapshot.elements,
        target_elements=target_snapshot.elements,
        base_version=base,
        target_version=target
    )

    return diff_result

@router.post("/{project_id}/diff/analyze", response_model=dict)
async def analyze_conflicts(project_id: str, analysis_data: dict):
    return {
        "hasConflicts": False,
        "conflicts": [],
        "safeChanges": []
    }
