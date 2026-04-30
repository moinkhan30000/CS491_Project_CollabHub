"""
Merge Router
"""

from fastapi import APIRouter, HTTPException, status, Depends
from entities.user_entity import User
from dependencies import get_current_user
from schemas.diff_schema import (
    MergeRequest, MergeResult,
    PullRequest, PullResult,
    Merge3WayRequest, Merge3WayResult,
)
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository
from diff_engine import DiffEngine

router = APIRouter()
diff_engine = DiffEngine()
project_repo = ProjectRepository()
commit_repo = CommitRepository()


@router.post("/{project_id}/merge", response_model=MergeResult)
async def merge_commits(
    project_id: str,
    merge_request: MergeRequest,
    current_user: User = Depends(get_current_user),
):
    """Request a merge operation (3-way merge if possible)"""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    base_snapshot   = commit_repo.get_snapshot(merge_request.baseCommit)
    source_snapshot = commit_repo.get_snapshot(merge_request.sourceCommit)
    target_snapshot = commit_repo.get_snapshot(merge_request.targetCommit)

    if not all([base_snapshot, source_snapshot, target_snapshot]):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="One or more commits not found")

    source_diff = diff_engine.compute_diff(
        base_elements=base_snapshot.elements,
        target_elements=source_snapshot.elements,
        base_version=merge_request.baseCommit,
        target_version=merge_request.sourceCommit,
    )
    target_diff = diff_engine.compute_diff(
        base_elements=base_snapshot.elements,
        target_elements=target_snapshot.elements,
        base_version=merge_request.baseCommit,
        target_version=merge_request.targetCommit,
    )

    conflicts = diff_engine.detect_conflicts(
        local_changes=source_diff.changes,
        remote_changes=target_diff.changes,
    )

    if conflicts and not merge_request.resolutions:
        return MergeResult(
            mergeCommitId="",
            status="conflict",
            appliedChanges=0,
            skippedChanges=0,
            conflicts=conflicts,
        )

    return MergeResult(
        mergeCommitId="merge-commit-id",
        status="success",
        appliedChanges=len(source_diff.changes) + len(target_diff.changes),
        skippedChanges=0,
        conflicts=[],
    )


@router.post("/{project_id}/pull", response_model=PullResult)
async def pull_changes(
    project_id: str,
    pull_request: PullRequest,
    current_user: User = Depends(get_current_user),
):
    """Pull changes from a specific commit."""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    chain_changes = commit_repo.get_linear_chain_deltas(
        current_commit_id=pull_request.currentCommit,
        target_commit_id=pull_request.targetCommit,
    )

    if chain_changes is not None:
        if pull_request.selectiveElements:
            chain_changes = [
                c for c in chain_changes
                if c.elementId in pull_request.selectiveElements
            ]
        return PullResult(
            changes=chain_changes,
            conflicts=[],
            requiresResolution=False,
        )

    current_snapshot = commit_repo.get_snapshot(pull_request.currentCommit)
    target_snapshot  = commit_repo.get_snapshot(pull_request.targetCommit)

    if not current_snapshot or not target_snapshot:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="One or more commits not found",
        )

    diff_result = diff_engine.compute_diff(
        base_elements=current_snapshot.elements,
        target_elements=target_snapshot.elements,
        base_version=pull_request.currentCommit,
        target_version=pull_request.targetCommit,
    )

    changes = diff_result.changes
    if pull_request.selectiveElements:
        changes = [c for c in changes if c.elementId in pull_request.selectiveElements]

    return PullResult(
        changes=changes,
        conflicts=diff_result.conflicts,
        requiresResolution=len(diff_result.conflicts) > 0,
    )


@router.post("/{project_id}/merge3way", response_model=Merge3WayResult)
async def merge_3way(
    project_id: str,
    request: Merge3WayRequest,
    current_user: User = Depends(get_current_user),
):
    """3-way merge analysis between two branch heads.

    Finds the common ancestor, computes diffs from both branches,
    and detects conflicts (concurrent modifications, delete-vs-modify,
    and spatial collisions).
    """
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    ancestor_id = commit_repo.find_common_ancestor(
        request.sourceCommitId, request.targetCommitId
    )
    if not ancestor_id:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail="Could not find a common ancestor between the two commits.",
        )

    ancestor_snapshot = commit_repo.get_snapshot(ancestor_id)
    source_snapshot = commit_repo.get_snapshot(request.sourceCommitId)
    target_snapshot = commit_repo.get_snapshot(request.targetCommitId)

    if not all([ancestor_snapshot, source_snapshot, target_snapshot]):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Could not reconstruct snapshots for one or more commits.",
        )

    source_diff = diff_engine.compute_diff(
        base_elements=ancestor_snapshot.elements,
        target_elements=source_snapshot.elements,
        base_version=ancestor_id,
        target_version=request.sourceCommitId,
    )
    target_diff = diff_engine.compute_diff(
        base_elements=ancestor_snapshot.elements,
        target_elements=target_snapshot.elements,
        base_version=ancestor_id,
        target_version=request.targetCommitId,
    )

    mod_conflicts = diff_engine.detect_conflicts(
        local_changes=source_diff.changes,
        remote_changes=target_diff.changes,
    )

    spatial_conflicts = diff_engine.detect_spatial_collisions(
        source_changes=source_diff.changes,
        target_changes=target_diff.changes,
    )

    all_conflicts = mod_conflicts + spatial_conflicts

    return Merge3WayResult(
        commonAncestorId=ancestor_id,
        sourceChanges=source_diff.changes,
        targetChanges=target_diff.changes,
        conflicts=all_conflicts,
        hasConflicts=len(all_conflicts) > 0,
    )