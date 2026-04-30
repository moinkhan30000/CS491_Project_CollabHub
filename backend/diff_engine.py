"""
Diff Engine - Core logic for comparing element snapshots
"""

from typing import Dict, List, Set
from schemas.element_schema import Element
from schemas.diff_schema import Change, ParameterChange, Conflict, DiffResult
from datetime import datetime

class DiffEngine:
    """Compute differences between two element snapshots"""
    
    def __init__(self):
        self.changes: List[Change] = []
        self.conflicts: List[Conflict] = []
    
    def compute_diff(
        self,
        base_elements: List[Element],
        target_elements: List[Element],
        base_version: str,
        target_version: str
    ) -> DiffResult:
        """
        Compute differences between base and target snapshots
        
        Args:
            base_elements: List of elements from base snapshot
            target_elements: List of elements from target snapshot
            base_version: Base commit ID
            target_version: Target commit ID
            
        Returns:
            DiffResult containing all changes and conflicts
        """
        self.changes = []
        self.conflicts = []
        
        matched_pairs, added_elements, deleted_elements = self._match_elements(
            base_elements,
            target_elements,
        )

        # Find added elements
        for elem in added_elements:
            self.changes.append(Change(
                changeType="added",
                elementId=elem.id,
                repoGuid=elem.repoGuid,
                category=elem.category,
                type=elem.type,
                parameterChanges=[],
                geometryChanged=False,
                locationChanged=False,
                oldData=None,
                newData=elem.model_dump()
            ))
        
        # Find deleted elements
        for elem in deleted_elements:
            self.changes.append(Change(
                changeType="deleted",
                elementId=elem.id,
                repoGuid=elem.repoGuid,
                category=elem.category,
                type=elem.type,
                parameterChanges=[],
                geometryChanged=False,
                locationChanged=False,
                oldData=elem.model_dump(),
                newData=None
            ))
        
        # Find modified elements
        for base_elem, target_elem in matched_pairs:
            change = self._compare_elements(base_elem, target_elem)
            if change:
                self.changes.append(change)
        
        # Compute summary
        summary = {
            "added": len(added_elements),
            "modified": len([c for c in self.changes if c.changeType == "modified"]),
            "deleted": len(deleted_elements),
            "total": len(self.changes)
        }
        
        return DiffResult(
            baseVersion=base_version,
            targetVersion=target_version,
            timestamp=datetime.utcnow(),
            summary=summary,
            changes=self.changes,
            conflicts=self.conflicts
        )
    
    def _compare_elements(self, base: Element, target: Element) -> Change | None:
        """Compare two elements and return change if different"""
        
        param_changes = self._compare_parameters(base.parameters, target.parameters)
        geometry_changed = self._compare_geometry(base.geometry, target.geometry)
        location_changed = self._compare_location(base.location, target.location)
        
        # If nothing changed, return None
        if not param_changes and not geometry_changed and not location_changed:
            return None
        
        return Change(
            changeType="modified",
            elementId=base.id,
            repoGuid=base.repoGuid or target.repoGuid,
            category=base.category,
            type=base.type,
            parameterChanges=param_changes,
            geometryChanged=geometry_changed,
            locationChanged=location_changed,
            oldData=base.model_dump(),
            newData=target.model_dump()
        )
    
    def _compare_parameters(
        self,
        base_params: Dict,
        target_params: Dict
    ) -> List[ParameterChange]:
        """Compare parameters and return list of changes"""
        changes = []
        
        all_param_names = set(base_params.keys()) | set(target_params.keys())
        
        for param_name in all_param_names:
            base_param = base_params.get(param_name)
            target_param = target_params.get(param_name)
            
            # Parameter added
            if base_param is None and target_param is not None:
                changes.append(ParameterChange(
                    name=param_name,
                    oldValue=None,
                    newValue=target_param.value,
                    type=target_param.type,
                    elementName=target_param.elementName
                ))
            
            # Parameter deleted
            elif base_param is not None and target_param is None:
                changes.append(ParameterChange(
                    name=param_name,
                    oldValue=base_param.value,
                    newValue=None,
                    type=base_param.type,
                    elementName=base_param.elementName
                ))
            
            # Parameter modified
            elif base_param is not None and target_param is not None:
                if base_param.value != target_param.value:
                    changes.append(ParameterChange(
                        name=param_name,
                        oldValue=base_param.value,
                        newValue=target_param.value,
                        type=target_param.type,
                        elementName=target_param.elementName or base_param.elementName
                    ))
        
        return changes
    
    def _compare_geometry(self, base_geom, target_geom) -> bool:
        """Check if geometry changed"""
        if base_geom is None and target_geom is None:
            return False
        
        if base_geom is None or target_geom is None:
            return True
        
        # Compare geometry hash
        if base_geom.geometryHash != target_geom.geometryHash:
            return True
        
        return False
    
    def _compare_location(self, base_loc, target_loc) -> bool:
        """Check if location changed"""
        if base_loc is None and target_loc is None:
            return False
        
        if base_loc is None or target_loc is None:
            return True
        
        # Convert to dict and compare
        base_dict = base_loc.model_dump()
        target_dict = target_loc.model_dump()
        
        return base_dict != target_dict
    
    def detect_conflicts(
        self,
        local_changes: List[Change],
        remote_changes: List[Change]
    ) -> List[Conflict]:
        """Detect conflicts between local and remote changes"""
        conflicts = []
        
        # Create lookup by element ID
        local_dict = {self._change_identity(c): c for c in local_changes}
        remote_dict = {self._change_identity(c): c for c in remote_changes}
        
        common_ids = set(local_dict.keys()) & set(remote_dict.keys())
        
        for elem_id in common_ids:
            local = local_dict[elem_id]
            remote = remote_dict[elem_id]
            
            # Both modified the same element
            if local.changeType == "modified" and remote.changeType == "modified":
                conflicts.append(Conflict(
                    elementId=elem_id,
                    conflictType="concurrent_modification",
                    description=f"Element {elem_id} modified in both local and remote",
                    localChange=local.model_dump(),
                    remoteChange=remote.model_dump(),
                    resolutionOptions=["keep_local", "accept_remote", "manual_resolve"]
                ))
            
            # One deleted, one modified
            elif (local.changeType == "deleted" and remote.changeType == "modified") or \
                 (local.changeType == "modified" and remote.changeType == "deleted"):
                conflicts.append(Conflict(
                    elementId=elem_id,
                    conflictType="delete_modified",
                    description=f"Element {elem_id} deleted in one version but modified in other",
                    localChange=local.model_dump(),
                    remoteChange=remote.model_dump(),
                    resolutionOptions=["keep_local", "accept_remote"]
                ))
        
        return conflicts
    
    def apply_selective_changes(
        self,
        base_elements: List[Element],
        changes: List[Change],
        selected_element_ids: Set[str]
    ) -> List[Element]:
        """
        Apply only selected changes to base elements
        
        Args:
            base_elements: Original elements
            changes: All available changes
            selected_element_ids: IDs of elements to apply changes for
            
        Returns:
            Updated list of elements
        """
        result_dict = {
            self._element_identity(elem): elem for elem in base_elements
        }
        
        for change in changes:
            # Skip if not selected
            if change.elementId not in selected_element_ids:
                continue
            
            identity = self._change_identity(change)
            if change.changeType == "added":
                # Add new element from newData
                if change.newData:
                    result_dict[identity] = Element(**change.newData)
            
            elif change.changeType == "deleted":
                # Remove element
                result_dict.pop(self._find_result_key(result_dict, change), None)
            
            elif change.changeType == "modified":
                # Update element with newData
                result_key = self._find_result_key(result_dict, change)
                if change.newData and result_key in result_dict:
                    # Merge changes - this is simplified; production would be more sophisticated
                    updated = Element(**change.newData)
                    del result_dict[result_key]
                    result_dict[self._element_identity(updated)] = updated
        
        return list(result_dict.values())

    def apply_changes(
        self,
        base_elements: List[Element],
        changes: List[Change]
    ) -> List[Element]:
        """
        Apply a list of changes to base elements.
    
        This is the core function for reconstructing a new state from:
        - A starting state (base_elements)
        - A list of modifications (changes)
    
        Args:
            base_elements: List of elements to start with
            changes: List of Change objects (added/modified/deleted)
        
        Returns:
            Final list of elements after all changes applied
        
        Example:
            base = [wall-1, wall-2, wall-3]
            changes = [
                Change(added, wall-4),
                Change(deleted, wall-2),
                Change(modified, wall-1, height=4.0)
            ]
            result = engine.apply_changes(base, changes)
            # Returns: [wall-1(modified), wall-3, wall-4]
        """
        # Convert to dict for fast O(1) lookup and modification
        result_dict = {
            self._element_identity(elem): elem for elem in base_elements
        }
    
        # Apply each change in order
        for change in changes:
            try:
                identity = self._change_identity(change)
                if change.changeType == "added":
                    # Create new element from newData
                    if change.newData:
                        new_element = Element(**change.newData)
                        result_dict[self._element_identity(new_element)] = new_element
            
                elif change.changeType == "deleted":
                    # Remove element if it exists
                    result_key = self._find_result_key(result_dict, change)
                    if result_key in result_dict:
                        del result_dict[result_key]
            
                elif change.changeType == "modified":
                    # Replace element with updated version
                    result_key = self._find_result_key(result_dict, change)
                    if change.newData and result_key in result_dict:
                        updated_element = Element(**change.newData)
                        del result_dict[result_key]
                        result_dict[self._element_identity(updated_element)] = updated_element
        
            except Exception as e:
                # Log error but continue processing
                print(f"Warning: Failed to apply change for {change.elementId}: {str(e)}")
                continue
    
        # Return as list (order doesn't matter, but keep consistent)
        return list(result_dict.values())

    @staticmethod
    def _element_identity(element: Element) -> str:
        return element.repoGuid or element.id

    @staticmethod
    def _change_identity(change: Change) -> str:
        return change.repoGuid or change.elementId

    def _find_result_key(self, result_dict: Dict[str, Element], change: Change) -> str | None:
        identity = self._change_identity(change)
        if identity in result_dict:
            return identity

        for key, element in result_dict.items():
            if change.repoGuid and element.repoGuid == change.repoGuid:
                return key
            if element.id == change.elementId:
                return key

        return None

    def _match_elements(
        self,
        base_elements: List[Element],
        target_elements: List[Element],
    ) -> tuple[list[tuple[Element, Element]], list[Element], list[Element]]:
        matched_pairs: list[tuple[Element, Element]] = []
        matched_base: set[int] = set()
        matched_target: set[int] = set()

        base_by_repo_guid = {
            elem.repoGuid: (idx, elem)
            for idx, elem in enumerate(base_elements)
            if elem.repoGuid
        }
        target_by_repo_guid = {
            elem.repoGuid: (idx, elem)
            for idx, elem in enumerate(target_elements)
            if elem.repoGuid
        }

        for repo_guid in set(base_by_repo_guid.keys()) & set(target_by_repo_guid.keys()):
            base_idx, base_elem = base_by_repo_guid[repo_guid]
            target_idx, target_elem = target_by_repo_guid[repo_guid]
            matched_base.add(base_idx)
            matched_target.add(target_idx)
            matched_pairs.append((base_elem, target_elem))

        base_by_id = {
            elem.id: (idx, elem)
            for idx, elem in enumerate(base_elements)
            if idx not in matched_base
        }
        target_by_id = {
            elem.id: (idx, elem)
            for idx, elem in enumerate(target_elements)
            if idx not in matched_target
        }

        for element_id in set(base_by_id.keys()) & set(target_by_id.keys()):
            base_idx, base_elem = base_by_id[element_id]
            target_idx, target_elem = target_by_id[element_id]
            matched_base.add(base_idx)
            matched_target.add(target_idx)
            matched_pairs.append((base_elem, target_elem))

        added = [
            elem for idx, elem in enumerate(target_elements)
            if idx not in matched_target
        ]
        deleted = [
            elem for idx, elem in enumerate(base_elements)
            if idx not in matched_base
        ]
        return matched_pairs, added, deleted

    def detect_spatial_collisions(
        self,
        source_changes: List[Change],
        target_changes: List[Change],
    ) -> List[Conflict]:
        """
        Detect potential spatial collisions between elements added on
        different branches. Uses location point proximity as a heuristic.
        Exact geometry intersection is performed on the Revit plugin side.
        """
        conflicts = []

        source_adds = [c for c in source_changes if c.changeType == "added" and c.newData]
        target_adds = [c for c in target_changes if c.changeType == "added" and c.newData]

        if not source_adds or not target_adds:
            return conflicts

        for s_change in source_adds:
            s_loc = self._extract_location_point(s_change.newData)
            if s_loc is None:
                continue

            for t_change in target_adds:
                t_loc = self._extract_location_point(t_change.newData)
                if t_loc is None:
                    continue

                # Check proximity — if two elements are within 1 foot
                # of each other, flag for exact check on the plugin side
                dist = (
                    (s_loc[0] - t_loc[0]) ** 2 +
                    (s_loc[1] - t_loc[1]) ** 2 +
                    (s_loc[2] - t_loc[2]) ** 2
                ) ** 0.5

                if dist < 1.0:  # 1 foot threshold
                    conflicts.append(Conflict(
                        elementId=f"{self._change_identity(s_change)}|{self._change_identity(t_change)}",
                        conflictType="spatial_collision",
                        description=(
                            f"Potential spatial collision: "
                            f"{s_change.category} ({s_change.elementId}) from source "
                            f"overlaps with {t_change.category} ({t_change.elementId}) from target"
                        ),
                        localChange=s_change.model_dump(),
                        remoteChange=t_change.model_dump(),
                        resolutionOptions=["keep_local", "accept_remote", "keep_both"],
                    ))

        return conflicts

    @staticmethod
    def _extract_location_point(data: dict) -> list | None:
        """Extract [x, y, z] from element data's location field."""
        if not data:
            return None
        loc = data.get("location")
        if not isinstance(loc, dict):
            return None

        loc_type = loc.get("type")
        if loc_type == "point":
            pt = loc.get("point", {})
            return [
                float(pt.get("x", 0)),
                float(pt.get("y", 0)),
                float(pt.get("z", 0)),
            ]
        elif loc_type == "curve":
            sp = loc.get("startPoint", {})
            ep = loc.get("endPoint", {})
            # Use midpoint of curve
            return [
                (float(sp.get("x", 0)) + float(ep.get("x", 0))) / 2,
                (float(sp.get("y", 0)) + float(ep.get("y", 0))) / 2,
                (float(sp.get("z", 0)) + float(ep.get("z", 0))) / 2,
            ]
        return None

