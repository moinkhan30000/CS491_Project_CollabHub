"""
Merge Router
"""

from fastapi import APIRouter, HTTPException, status, Depends
from typing import Optional
from entities.user_entity import User
from dependencies import get_current_user
from schemas.diff_schema import MergeRequest, MergeResult, PullRequest, PullResult
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository 
from diff_engine import DiffEngine

router = APIRouter()
diff_engine = DiffEngine()
project_repo = ProjectRepository()
commit_repo = CommitRepository()


def find_common_ancestor(commit1_id: str, commit2_id: str) -> Optional[str]:
    """Find the most recent common ancestor between two commits."""
    if not commit1_id or not commit2_id:
        return None
        
    ancestors = set()
    current = commit1_id
    while current:
        ancestors.add(current)
        commit = commit_repo.get_commit(current)
        if not commit:
            break
        current = commit.parentCommit
        
    current = commit2_id
    while current:
        if current in ancestors:
            return current
        commit = commit_repo.get_commit(current)
        if not commit:
            break
        current = commit.parentCommit
        
    return None


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

    # Phase 1: Base Commit Discovery
    base_commit_id = merge_request.baseCommit
    if not base_commit_id:
        base_commit_id = find_common_ancestor(merge_request.sourceCommit, merge_request.targetCommit)
        if not base_commit_id:
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="No common ancestor found (unable to merge).")

    base_snapshot   = commit_repo.get_snapshot(base_commit_id)
    source_snapshot = commit_repo.get_snapshot(merge_request.sourceCommit)
    target_snapshot = commit_repo.get_snapshot(merge_request.targetCommit)

    if not all([base_snapshot, source_snapshot, target_snapshot]):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="One or more layer commits not found")

    # Target = Local (The branch we are on)
    target_diff = diff_engine.compute_diff(
        base_elements=base_snapshot.elements,
        target_elements=target_snapshot.elements,
        base_version=base_commit_id,
        target_version=merge_request.targetCommit,
    )
    
    # Source = Remote (The branch we are pulling from / merging in)
    source_diff = diff_engine.compute_diff(
        base_elements=base_snapshot.elements,
        target_elements=source_snapshot.elements,
        base_version=base_commit_id,
        target_version=merge_request.sourceCommit,
    )

    # Find Collisions
    conflicts = diff_engine.detect_conflicts(
        local_changes=target_diff.changes,
        remote_changes=source_diff.changes,
    )

    # Phase 1: If there are conflicts and no resolutions, bounce it back to the UI
    if conflicts and not merge_request.resolutions:
        return MergeResult(
            mergeCommitId="",
            status="conflict",
            appliedChanges=0,
            skippedChanges=0,
            conflicts=conflicts,
        )

    # Phase 2: Processing User Resolutions 
    final_changes = []
    skipped_changes = 0

    target_map = { (c.repoGuid or c.elementId): c for c in target_diff.changes }
    source_map = { (c.repoGuid or c.elementId): c for c in source_diff.changes }
    
    all_ids = set(target_map.keys()) | set(source_map.keys())
    conflict_ids = { c.elementId for c in conflicts }
    resolved_dict = { r.elementId: r.resolution for r in merge_request.resolutions }

    for eid in all_ids:
        in_target = eid in target_map
        in_source = eid in source_map

        if eid in conflict_ids:
            # Check user choice for the conflicted item
            res = resolved_dict.get(eid)
            
            if not res or res == "manual_resolve":
                skipped_changes += 1
                continue
            
            if res == "keep_local" and in_target:
                final_changes.append(target_map[eid])
            elif res == "accept_remote" and in_source:
                final_changes.append(source_map[eid])
                
        else:
            # Safe elements (changed in one branch but not the other)
            if in_target and not in_source:
                final_changes.append(target_map[eid])
            elif in_source and not in_target:
                final_changes.append(source_map[eid])
            else:
                # Should not happen logically, but default to target mapping
                if in_target:
                    final_changes.append(target_map[eid])

    # Phase 3: Structural Commit Creation (Squash and Merge)
    
    # 1. Apply our filtered final instructions to the Base to get the "Perfectly Merged Model State"
    unified_elements = diff_engine.apply_changes(base_snapshot.elements, final_changes)
    
    # 2. Calculate the delta from Target (where the user actually is) forward to the Merged State
    squash_diff_result = diff_engine.compute_diff(
        base_elements=target_snapshot.elements,
        target_elements=unified_elements,
        base_version=merge_request.targetCommit,
        target_version="merged_state"
    )
    
    target_db_commit = commit_repo.get_commit(merge_request.targetCommit)
    
    # 3. Create the Database Commit pointing cleanly only to Target
    new_commit = commit_repo.create_commit(
        project_id=project_id,
        model_id=target_snapshot.modelId,
        message=merge_request.message or f"Merge from commit {merge_request.sourceCommit}",
        author=current_user.email,
        change_type="MOD",
        parent_commit=merge_request.targetCommit,          # Squash makes this linearly on top of Target
        diff=squash_diff_result.changes,                   # The precise delta needed to advance Target
        branch_name=target_db_commit.branchName if target_db_commit else None
    )
    
    return MergeResult(
        mergeCommitId=new_commit.commitId,
        status="success",
        appliedChanges=len(squash_diff_result.changes),
        skippedChanges=skipped_changes,
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

    # ------------------------------------------------------------------
    # FAST PATH — walk the linear delta chain from target back to current.
    # Covers both pulling a single next commit and pulling multiple commits
    # at once (e.g. user on commit-100 pulling commit-105 after 5 pushes).
    # Cost: one DB read per commit in the gap, no reconstruction at all.
    # ------------------------------------------------------------------
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

    # ------------------------------------------------------------------
    # SLOW PATH — non-linear range (Divergent branches / Merge required)
    # ------------------------------------------------------------------
    current_snapshot = commit_repo.get_snapshot(pull_request.currentCommit)
    target_snapshot  = commit_repo.get_snapshot(pull_request.targetCommit)

    if not current_snapshot or not target_snapshot:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="One or more commits not found")

    # 1. We MUST find the common ancestor to check if this is a true merge
    base_commit_id = find_common_ancestor(pull_request.currentCommit, pull_request.targetCommit)

    # 2. If it's divergent (neither a fast-forward nor a straight rollback), enforce a 3-way merge!
    if base_commit_id and base_commit_id != pull_request.currentCommit and base_commit_id != pull_request.targetCommit:
        base_snapshot = commit_repo.get_snapshot(base_commit_id)
        
        # Calculate isolated branch changes
        local_diff = diff_engine.compute_diff(base_snapshot.elements, current_snapshot.elements, base_commit_id, pull_request.currentCommit)
        remote_diff = diff_engine.compute_diff(base_snapshot.elements, target_snapshot.elements, base_commit_id, pull_request.targetCommit)
        
        # Detect physical collisions
        conflicts = diff_engine.detect_conflicts(local_diff.changes, remote_diff.changes)
        
        # FORCE the system into a Merge state (even if conflicts=[] so safe auto-merge triggers)
        return PullResult(
            changes=[], # Prevent them from applying changes unsafely
            conflicts=conflicts,
            requiresResolution=True 
        )

    # 3. Fallback for pure Rollbacks (or if no ancestor exists)
    diff_result = diff_engine.compute_diff(current_snapshot.elements, target_snapshot.elements, pull_request.currentCommit, pull_request.targetCommit)

    changes = diff_result.changes
    if pull_request.selectiveElements:
        changes = [c for c in changes if c.elementId in pull_request.selectiveElements]

    return PullResult(
        changes=changes,
        conflicts=[],
        requiresResolution=False
    )
