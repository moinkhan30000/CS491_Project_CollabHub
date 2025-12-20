"""
Diff Router
"""

from fastapi import APIRouter, HTTPException, status, Query
from schemas.diff_schema import DiffResult
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
    target: str = Query(..., description="Target commit ID")
):
    """Compare two commits and get differences"""
    
    # Verify project exists
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )
    
    # Get snapshots
    base_snapshot = commit_repo.get_snapshot(base)
    target_snapshot = commit_repo.get_snapshot(target)
    
    if not base_snapshot:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Base commit {base} not found"
        )
    
    if not target_snapshot:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Target commit {target} not found"
        )
    
    # Compute diff
    diff_result = diff_engine.compute_diff(
        base_elements=base_snapshot.elements,
        target_elements=target_snapshot.elements,
        base_version=base,
        target_version=target
    )
    
    return diff_result

@router.post("/{project_id}/diff/analyze", response_model=dict)
async def analyze_conflicts(project_id: str, analysis_data: dict):
    """Analyze potential conflicts between local changes and remote commits"""
    
    # This would implement conflict detection logic
    # For now, return a simplified response
    
    return {
        "hasConflicts": False,
        "conflicts": [],
        "safeChanges": []
    }
