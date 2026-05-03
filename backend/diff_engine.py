"""
Diff Engine - Core logic for comparing element snapshots
"""

from typing import Dict, List, Set, Tuple
from schemas.element_schema import Element
from schemas.diff_schema import Change, ParameterChange, Conflict, DiffResult
from services.spatial_conflict_detector import SpatialConflictDetector
from services.parameter_conflict_detector import ParameterConflictDetector
from datetime import datetime

class DiffEngine:
    """Compute differences between two element snapshots"""
    
    def __init__(self):
        self.changes: List[Change] = []
        self.conflicts: List[Conflict] = []
        self._spatial_detector = SpatialConflictDetector()
        self._param_detector = ParameterConflictDetector()
    
    def compute_diff(self, base_elements, target_elements, base_version, target_version) -> DiffResult:
        self.changes = []
        self.conflicts = []
        
        matched_pairs, added_elements, deleted_elements = self._match_elements(base_elements, target_elements)

        for elem in added_elements:
            self.changes.append(Change(
                changeType="added", elementId=elem.id, repoGuid=elem.repoGuid,
                category=elem.category, type=elem.type, parameterChanges=[],
                geometryChanged=False, locationChanged=False, oldData=None, newData=elem.model_dump()
            ))
        
        for elem in deleted_elements:
            self.changes.append(Change(
                changeType="deleted", elementId=elem.id, repoGuid=elem.repoGuid,
                category=elem.category, type=elem.type, parameterChanges=[],
                geometryChanged=False, locationChanged=False, oldData=elem.model_dump(), newData=None
            ))
        
        for base_elem, target_elem in matched_pairs:
            change = self._compare_elements(base_elem, target_elem)
            if change:
                self.changes.append(change)
        
        summary = {
            "added": len(added_elements),
            "modified": len([c for c in self.changes if c.changeType == "modified"]),
            "deleted": len(deleted_elements),
            "total": len(self.changes)
        }
        
        return DiffResult(
            baseVersion=base_version, targetVersion=target_version,
            timestamp=datetime.utcnow(), summary=summary,
            changes=self.changes, conflicts=self.conflicts
        )

    def detect_conflicts(self, local_changes: List[Change], remote_changes: List[Change]) -> List[Conflict]:
        """
        Detect conflicts between local and remote changes.
        Uses parameter-level analysis: auto-mergeable differences are NOT returned
        as conflicts. Delete-vs-modify conflicts are still handled here directly.
        """
        conflicts: List[Conflict] = []

        local_dict  = {self._change_identity(c): c for c in local_changes}
        remote_dict = {self._change_identity(c): c for c in remote_changes}
        common_ids  = set(local_dict.keys()) & set(remote_dict.keys())

        for elem_id in common_ids:
            local  = local_dict[elem_id]
            remote = remote_dict[elem_id]

            # Delete vs modify — parameter detector doesn't handle this
            if (local.changeType == "deleted") != (remote.changeType == "deleted"):
                conflicts.append(Conflict(
                    elementId=elem_id,
                    conflictType="delete_modified",
                    description=f"Element {elem_id} deleted in one branch but modified in the other",
                    localChange=local.model_dump(),
                    remoteChange=remote.model_dump(),
                    resolutionOptions=["keep_local", "accept_remote"],
                ))

        # Delegate modification analysis to the parameter detector
        param_conflicts, _ = self._param_detector.analyse(local_changes, remote_changes)
        conflicts.extend(param_conflicts)

        return conflicts

    def detect_conflicts_with_auto_merged(
        self,
        local_changes: List[Change],
        remote_changes: List[Change],
    ) -> Tuple[List[Conflict], List[Change], List[str]]:
        """
        Full conflict analysis returning three lists:
          conflicts     — changes that need user resolution
          auto_merged   — safely merged changes (different params on same element)
          both_deleted  — element IDs deleted on both branches (not a conflict,
                          informational — the delete is the agreed outcome)
        """
        conflicts: List[Conflict] = []
        both_deleted: List[str] = []

        local_dict  = {self._change_identity(c): c for c in local_changes}
        remote_dict = {self._change_identity(c): c for c in remote_changes}

        for elem_id in set(local_dict) & set(remote_dict):
            local  = local_dict[elem_id]
            remote = remote_dict[elem_id]

            # Both deleted — agreed outcome, not a conflict
            if local.changeType == "deleted" and remote.changeType == "deleted":
                both_deleted.append(elem_id)
                continue

            # One deleted, one modified
            if (local.changeType == "deleted") != (remote.changeType == "deleted"):
                conflicts.append(Conflict(
                    elementId=elem_id,
                    conflictType="delete_modified",
                    description=f"Element {elem_id} deleted in one branch but modified in the other",
                    localChange=local.model_dump(),
                    remoteChange=remote.model_dump(),
                    resolutionOptions=["keep_local", "accept_remote"],
                ))

        param_conflicts, auto_merged = self._param_detector.analyse(local_changes, remote_changes)
        conflicts.extend(param_conflicts)

        return conflicts, auto_merged, both_deleted

    def detect_spatial_collisions(self, source_changes: List[Change], target_changes: List[Change]) -> List[Conflict]:
        """
        Detect spatial collisions between elements added on different branches.
        Uses AABB overlap as the primary test with point-proximity fallback.
        """
        return self._spatial_detector.detect(source_changes, target_changes)

    # ------------------------------------------------------------------
    # All methods below are unchanged from the original
    # ------------------------------------------------------------------

    def _compare_elements(self, base: Element, target: Element) -> Change | None:
        param_changes = self._compare_parameters(base.parameters, target.parameters)
        geometry_changed = self._compare_geometry(base.geometry, target.geometry)
        location_changed = self._compare_location(base.location, target.location)
        if not param_changes and not geometry_changed and not location_changed:
            return None
        return Change(
            changeType="modified", elementId=base.id, repoGuid=base.repoGuid or target.repoGuid,
            category=base.category, type=base.type, parameterChanges=param_changes,
            geometryChanged=geometry_changed, locationChanged=location_changed,
            oldData=base.model_dump(), newData=target.model_dump()
        )

    def _compare_parameters(self, base_params: Dict, target_params: Dict) -> List[ParameterChange]:
        changes = []
        all_param_names = set(base_params.keys()) | set(target_params.keys())
        for param_name in all_param_names:
            base_param   = base_params.get(param_name)
            target_param = target_params.get(param_name)
            if base_param is None and target_param is not None:
                changes.append(ParameterChange(name=param_name, oldValue=None, newValue=target_param.value,
                                               type=target_param.type, elementName=target_param.elementName))
            elif base_param is not None and target_param is None:
                changes.append(ParameterChange(name=param_name, oldValue=base_param.value, newValue=None,
                                               type=base_param.type, elementName=base_param.elementName))
            elif base_param is not None and target_param is not None:
                if base_param.value != target_param.value:
                    changes.append(ParameterChange(name=param_name, oldValue=base_param.value,
                                                   newValue=target_param.value, type=target_param.type,
                                                   elementName=target_param.elementName or base_param.elementName))
        return changes

    def _compare_geometry(self, base_geom, target_geom) -> bool:
        if base_geom is None and target_geom is None: return False
        if base_geom is None or target_geom is None:  return True
        return base_geom.geometryHash != target_geom.geometryHash

    def _compare_location(self, base_loc, target_loc) -> bool:
        if base_loc is None and target_loc is None: return False
        if base_loc is None or target_loc is None:  return True
        return base_loc.model_dump() != target_loc.model_dump()

    def apply_selective_changes(self, base_elements, changes, selected_element_ids):
        result_dict = {self._element_identity(e): e for e in base_elements}
        for change in changes:
            if change.elementId not in selected_element_ids: continue
            identity = self._change_identity(change)
            if change.changeType == "added" and change.newData:
                result_dict[identity] = Element(**change.newData)
            elif change.changeType == "deleted":
                result_dict.pop(self._find_result_key(result_dict, change), None)
            elif change.changeType == "modified":
                key = self._find_result_key(result_dict, change)
                if change.newData and key in result_dict:
                    updated = Element(**change.newData)
                    del result_dict[key]
                    result_dict[self._element_identity(updated)] = updated
        return list(result_dict.values())

    def apply_changes(self, base_elements, changes):
        result_dict = {self._element_identity(e): e for e in base_elements}
        for change in changes:
            try:
                if change.changeType == "added" and change.newData:
                    new_elem = Element(**change.newData)
                    result_dict[self._element_identity(new_elem)] = new_elem
                elif change.changeType == "deleted":
                    key = self._find_result_key(result_dict, change)
                    if key in result_dict: del result_dict[key]
                elif change.changeType == "modified":
                    key = self._find_result_key(result_dict, change)
                    if change.newData and key in result_dict:
                        updated = Element(**change.newData)
                        del result_dict[key]
                        result_dict[self._element_identity(updated)] = updated
            except Exception as e:
                print(f"Warning: Failed to apply change for {change.elementId}: {str(e)}")
        return list(result_dict.values())

    @staticmethod
    def _element_identity(element: Element) -> str:
        return element.repoGuid or element.id

    @staticmethod
    def _change_identity(change: Change) -> str:
        return change.repoGuid or change.elementId

    def _find_result_key(self, result_dict, change):
        identity = self._change_identity(change)
        if identity in result_dict: return identity
        for key, element in result_dict.items():
            if change.repoGuid and element.repoGuid == change.repoGuid: return key
            if element.id == change.elementId: return key
        return None

    def _match_elements(self, base_elements, target_elements):
        matched_pairs, matched_base, matched_target = [], set(), set()
        base_by_rg  = {e.repoGuid: (i, e) for i, e in enumerate(base_elements)   if e.repoGuid}
        tgt_by_rg   = {e.repoGuid: (i, e) for i, e in enumerate(target_elements) if e.repoGuid}
        for rg in set(base_by_rg) & set(tgt_by_rg):
            bi, be = base_by_rg[rg]; ti, te = tgt_by_rg[rg]
            matched_base.add(bi); matched_target.add(ti); matched_pairs.append((be, te))
        base_by_id = {e.id: (i, e) for i, e in enumerate(base_elements)   if i not in matched_base}
        tgt_by_id  = {e.id: (i, e) for i, e in enumerate(target_elements) if i not in matched_target}
        for eid in set(base_by_id) & set(tgt_by_id):
            bi, be = base_by_id[eid]; ti, te = tgt_by_id[eid]
            matched_base.add(bi); matched_target.add(ti); matched_pairs.append((be, te))
        added   = [e for i, e in enumerate(target_elements) if i not in matched_target]
        deleted = [e for i, e in enumerate(base_elements)   if i not in matched_base]
        return matched_pairs, added, deleted

    @staticmethod
    def _extract_location_point(data: dict) -> list | None:
        if not data: return None
        loc = data.get("location")
        if not isinstance(loc, dict): return None
        loc_type = loc.get("type")
        if loc_type == "point":
            pt = loc.get("point", {})
            return [float(pt.get("x", 0)), float(pt.get("y", 0)), float(pt.get("z", 0))]
        if loc_type == "curve":
            sp = loc.get("startPoint", {}); ep = loc.get("endPoint", {})
            return [(float(sp.get("x",0))+float(ep.get("x",0)))/2,
                    (float(sp.get("y",0))+float(ep.get("y",0)))/2,
                    (float(sp.get("z",0))+float(ep.get("z",0)))/2]
        return None

