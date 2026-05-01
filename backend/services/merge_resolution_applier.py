"""
Merge Resolution Applier
========================
Given the full output of a 3-way merge analysis plus the user's resolution
choices, produces a single final List[Change] that represents the merged
state ready to be committed or applied.

Resolution semantics
--------------------
keep_local      - use the source-branch version of the element
accept_remote   - use the target-branch version of the element
keep_both       - spatial_collision only: include BOTH added elements
manual_resolve  - skip; the caller (frontend/apply path) will handle it

This module is intentionally standalone.
It has no dependency on routers, repositories, or the Revit plugin.
"""

from __future__ import annotations

from typing import Dict, List, Set

from schemas.diff_schema import Change, Conflict, Resolution


class MergeResolutionApplier:
    """
    Produces the final merged changeset from a 3-way merge analysis.

    Usage::

        applier = MergeResolutionApplier()
        merged_changes = applier.apply(
            source_changes=source_diff.changes,
            target_changes=target_diff.changes,
            auto_merged=auto_merged,
            both_deleted_ids=both_deleted,
            conflicts=all_conflicts,
            resolutions=merge_request.resolutions,
        )
    """

    def apply(
        self,
        source_changes: List[Change],
        target_changes: List[Change],
        auto_merged: List[Change],
        both_deleted_ids: List[str],
        conflicts: List[Conflict],
        resolutions: List[Resolution],
    ) -> List[Change]:
        """
        Return the final list of changes representing the merged state.

        Elements that have no conflict and appear only on one branch are
        included as-is.  Auto-merged elements replace individual branch
        versions.  Conflicted elements are resolved per the resolutions list.
        """
        resolution_map: Dict[str, str] = {r.elementId: r.resolution for r in resolutions}

        # Identities that are involved in a conflict (need explicit resolution)
        conflicted_ids: Set[str] = {_conflict_identity(c) for c in conflicts}

        # Identities that were auto-merged (already combined)
        auto_merged_ids: Set[str] = {_change_identity(c) for c in auto_merged}

        # Identities agreed-deleted on both branches
        both_deleted_set: Set[str] = set(both_deleted_ids)

        result: Dict[str, Change] = {}

        # 1. Non-conflicted, non-auto-merged source changes
        for change in source_changes:
            ident = _change_identity(change)
            if ident not in conflicted_ids and ident not in auto_merged_ids:
                result[ident] = change

        # 2. Non-conflicted, non-auto-merged target changes not already in source
        for change in target_changes:
            ident = _change_identity(change)
            if ident not in conflicted_ids and ident not in auto_merged_ids and ident not in result:
                result[ident] = change

        # 3. Auto-merged changes - overwrite any individual branch version
        for change in auto_merged:
            result[_change_identity(change)] = change

        # 4. Both-deleted - include the delete once (agreed outcome)
        for ident in both_deleted_set:
            # Find the delete change from either branch to get full metadata
            delete_change = _find_change_by_identity(ident, source_changes) \
                         or _find_change_by_identity(ident, target_changes)
            if delete_change:
                result[ident] = delete_change

        # 5. Conflicted elements - apply resolutions
        source_by_id = {_change_identity(c): c for c in source_changes}
        target_by_id = {_change_identity(c): c for c in target_changes}

        skipped: List[str] = []

        for conflict in conflicts:
            conflict_ident = _conflict_identity(conflict)
            resolution = resolution_map.get(conflict_ident, "manual_resolve")

            if resolution == "keep_local":
                change = _find_change_by_identity(conflict_ident, source_changes)
                if change:
                    result[conflict_ident] = change

            elif resolution == "accept_remote":
                change = _find_change_by_identity(conflict_ident, target_changes)
                if change:
                    result[conflict_ident] = change

            elif resolution == "keep_both":
                # Only valid for spatial_collision (both are "added" changes).
                # Include the source element under its own identity and the
                # target element under its own identity - they are distinct
                # elements that happen to overlap spatially.
                if conflict.conflictType == "spatial_collision":
                    src_ident, tgt_ident = _split_spatial_id(conflict.elementId)
                    src_change = source_by_id.get(src_ident)
                    tgt_change = target_by_id.get(tgt_ident)
                    if src_change:
                        result[src_ident] = src_change
                    if tgt_change:
                        result[tgt_ident] = tgt_change
                else:
                    # keep_both on non-spatial conflict is not meaningful -
                    # fall back to keep_local so we never silently drop a change
                    change = _find_change_by_identity(conflict_ident, source_changes)
                    if change:
                        result[conflict_ident] = change

            else:
                # manual_resolve or unknown - caller is responsible
                skipped.append(conflict_ident)

        return list(result.values())

    def count_skipped(
        self,
        conflicts: List[Conflict],
        resolutions: List[Resolution],
    ) -> int:
        """Return the number of conflicts left to manual_resolve."""
        resolution_map = {r.elementId: r.resolution for r in resolutions}
        return sum(
            1 for c in conflicts
            if resolution_map.get(_conflict_identity(c), "manual_resolve") == "manual_resolve"
        )


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _change_identity(change: Change) -> str:
    return change.repoGuid or change.elementId


def _conflict_identity(conflict: Conflict) -> str:
    """
    For spatial collisions the elementId is 'sourceId|targetId'.
    Return the full compound id so resolution lookups work correctly.
    """
    return conflict.elementId


def _split_spatial_id(compound_id: str):
    """Split a spatial collision compound id into (source_id, target_id)."""
    parts = compound_id.split("|", 1)
    if len(parts) == 2:
        return parts[0], parts[1]
    return compound_id, compound_id


def _find_change_by_identity(identity: str, changes: List[Change]):
    for c in changes:
        if _change_identity(c) == identity:
            return c
    return None