"""
Parameter Conflict Detector
============================
Performs parameter-level analysis on pairs of Changes that touch the same
element on two different branches.

Three outcomes per element:

  1. TRUE CONFLICT   both branches changed the same parameter to
                      different values : Conflict(parameter_conflict)

  2. AUTO-MERGE      both branches changed the element but on completely
                      different parameters : a merged Change is returned,
                      no Conflict raised

  3. GEOMETRY/LOCATION CONFLICT  both branches moved or reshaped the same
                      element : Conflict(concurrent_modification) as before,
                      because spatial resolution requires user judgement

This module is intentionally standalone.  It has no imports from other
services and can be tested in isolation.
"""

from __future__ import annotations

from typing import Dict, List, Optional, Tuple

from schemas.diff_schema import Change, Conflict, ParameterChange


class ParameterConflictDetector:
    """
    Analyses pairs of same-element modifications across two branch diffs.

    Usage::

        detector = ParameterConflictDetector()
        conflicts, auto_merged = detector.analyse(source_changes, target_changes)
    """

    def analyse(
        self,
        source_changes: List[Change],
        target_changes: List[Change],
    ) -> Tuple[List[Conflict], List[Change]]:
        """
        Compare same-element modifications across two branches.

        Returns:
            conflicts     Conflict objects that need user resolution
            auto_merged   Change objects that were safely merged and need
                           no user input
        """
        source_mods: Dict[str, Change] = {
            _identity(c): c for c in source_changes if c.changeType == "modified"
        }
        target_mods: Dict[str, Change] = {
            _identity(c): c for c in target_changes if c.changeType == "modified"
        }

        conflicts: List[Conflict] = []
        auto_merged: List[Change] = []

        for elem_id in set(source_mods) & set(target_mods):
            src = source_mods[elem_id]
            tgt = target_mods[elem_id]

            conflict, merged = self._analyse_pair(elem_id, src, tgt)

            if conflict:
                conflicts.append(conflict)
            elif merged:
                auto_merged.append(merged)

        return conflicts, auto_merged

    # ------------------------------------------------------------------
    # Internal
    # ------------------------------------------------------------------

    def _analyse_pair(
        self,
        elem_id: str,
        src: Change,
        tgt: Change,
    ) -> Tuple[Optional[Conflict], Optional[Change]]:
        """
        Analyse one element that was modified on both branches.

        Returns (conflict, auto_merged_change) � exactly one of the two
        will be non-None, or both None if there is nothing actionable.
        """
        # Geometry or location conflict � requires user judgement
        if src.geometryChanged and tgt.geometryChanged:
            return _make_conflict(
                elem_id=elem_id,
                conflict_type="concurrent_modification",
                description=(
                    f"Element {elem_id} has geometry changes on both branches � "
                    "manual resolution required."
                ),
                src=src,
                tgt=tgt,
                conflicting_params=None,
            ), None

        if src.locationChanged and tgt.locationChanged:
            return _make_conflict(
                elem_id=elem_id,
                conflict_type="concurrent_modification",
                description=(
                    f"Element {elem_id} was moved on both branches � "
                    "manual resolution required."
                ),
                src=src,
                tgt=tgt,
                conflicting_params=None,
            ), None

        # Parameter-level analysis
        src_params: Dict[str, ParameterChange] = {p.name: p for p in src.parameterChanges}
        tgt_params: Dict[str, ParameterChange] = {p.name: p for p in tgt.parameterChanges}

        clashing = _find_clashing_params(src_params, tgt_params)

        if clashing:
            return _make_conflict(
                elem_id=elem_id,
                conflict_type="parameter_conflict",
                description=(
                    f"Element {elem_id} has conflicting parameter changes on both branches: "
                    + ", ".join(clashing)
                ),
                src=src,
                tgt=tgt,
                conflicting_params=clashing,
            ), None

        # No clashing params � auto-merge: combine both param lists
        merged_change = _auto_merge(src, tgt)
        return None, merged_change


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _identity(change: Change) -> str:
    return change.repoGuid or change.elementId


def _find_clashing_params(
    src_params: Dict[str, ParameterChange],
    tgt_params: Dict[str, ParameterChange],
) -> List[str]:
    """Return names of parameters changed on both branches to different values."""
    clashing = []
    for name in set(src_params) & set(tgt_params):
        src_val = src_params[name].newValue
        tgt_val = tgt_params[name].newValue
        if src_val != tgt_val:
            clashing.append(name)
    return clashing


def _make_conflict(
    elem_id: str,
    conflict_type: str,
    description: str,
    src: Change,
    tgt: Change,
    conflicting_params: Optional[List[str]],
) -> Conflict:
    return Conflict(
        elementId=elem_id,
        conflictType=conflict_type,
        description=description,
        localChange=src.model_dump(),
        remoteChange=tgt.model_dump(),
        resolutionOptions=["keep_local", "accept_remote", "manual_resolve"],
        conflictingParams=conflicting_params,
    )


def _auto_merge(src: Change, tgt: Change) -> Change:
    """
    Produce a single merged Change by combining non-overlapping param changes
    from both branches.  The target branch wins for geometry/location flags
    since they were checked for conflict above.
    """
    src_params: Dict[str, ParameterChange] = {p.name: p for p in src.parameterChanges}
    tgt_params: Dict[str, ParameterChange] = {p.name: p for p in tgt.parameterChanges}

    # Start with source params, overlay target params (both non-clashing by this point)
    merged_params = {**src_params, **tgt_params}

    return Change(
        changeType="modified",
        elementId=src.elementId,
        repoGuid=src.repoGuid or tgt.repoGuid,
        category=src.category,
        type=src.type,
        parameterChanges=list(merged_params.values()),
        geometryChanged=src.geometryChanged or tgt.geometryChanged,
        locationChanged=src.locationChanged or tgt.locationChanged,
        oldData=src.oldData,
        newData=tgt.newData,  # target's final state is the merge target
    )