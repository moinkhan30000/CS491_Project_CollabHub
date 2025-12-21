"""
Diff Engine - Core logic for comparing element snapshots
"""

from typing import Dict, List, Set
from schemas.element_schema import Element
from schemas.diff_schema import Change, ParameterChange, Conflict, DiffResult
from datetime import datetime
import hashlib
import json

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
        
        # Create lookup dictionaries by element ID
        base_dict = {elem.id: elem for elem in base_elements}
        target_dict = {elem.id: elem for elem in target_elements}
        
        base_ids = set(base_dict.keys())
        target_ids = set(target_dict.keys())
        
        # Find added elements
        added_ids = target_ids - base_ids
        for elem_id in added_ids:
            elem = target_dict[elem_id]
            self.changes.append(Change(
                changeType="added",
                elementId=elem_id,
                category=elem.category,
                type=elem.type,
                parameterChanges=[],
                geometryChanged=False,
                locationChanged=False,
                oldData=None,
                newData=elem.model_dump()
            ))
        
        # Find deleted elements
        deleted_ids = base_ids - target_ids
        for elem_id in deleted_ids:
            elem = base_dict[elem_id]
            self.changes.append(Change(
                changeType="deleted",
                elementId=elem_id,
                category=elem.category,
                type=elem.type,
                parameterChanges=[],
                geometryChanged=False,
                locationChanged=False,
                oldData=elem.model_dump(),
                newData=None
            ))
        
        # Find modified elements
        common_ids = base_ids & target_ids
        for elem_id in common_ids:
            base_elem = base_dict[elem_id]
            target_elem = target_dict[elem_id]
            
            change = self._compare_elements(base_elem, target_elem)
            if change:
                self.changes.append(change)
        
        # Compute summary
        summary = {
            "added": len(added_ids),
            "modified": len([c for c in self.changes if c.changeType == "modified"]),
            "deleted": len(deleted_ids),
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
            category=base.category,
            type=base.type,
            parameterChanges=param_changes,
            geometryChanged=geometry_changed,
            locationChanged=location_changed,
            oldData={
                "parameters": {k: v.model_dump() for k, v in base.parameters.items()},
                "geometry": base.geometry.model_dump() if base.geometry else None,
                "location": base.location.model_dump() if base.location else None
            },
            newData={
                "parameters": {k: v.model_dump() for k, v in target.parameters.items()},
                "geometry": target.geometry.model_dump() if target.geometry else None,
                "location": target.location.model_dump() if target.location else None
            }
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
                    type=target_param.type
                ))
            
            # Parameter deleted
            elif base_param is not None and target_param is None:
                changes.append(ParameterChange(
                    name=param_name,
                    oldValue=base_param.value,
                    newValue=None,
                    type=base_param.type
                ))
            
            # Parameter modified
            elif base_param is not None and target_param is not None:
                if base_param.value != target_param.value:
                    changes.append(ParameterChange(
                        name=param_name,
                        oldValue=base_param.value,
                        newValue=target_param.value,
                        type=target_param.type
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
        local_dict = {c.elementId: c for c in local_changes}
        remote_dict = {c.elementId: c for c in remote_changes}
        
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
        result_dict = {elem.id: elem for elem in base_elements}
        
        for change in changes:
            # Skip if not selected
            if change.elementId not in selected_element_ids:
                continue
            
            if change.changeType == "added":
                # Add new element from newData
                if change.newData:
                    result_dict[change.elementId] = Element(**change.newData)
            
            elif change.changeType == "deleted":
                # Remove element
                result_dict.pop(change.elementId, None)
            
            elif change.changeType == "modified":
                # Update element with newData
                if change.newData and change.elementId in result_dict:
                    # Merge changes - this is simplified; production would be more sophisticated
                    result_dict[change.elementId] = Element(**change.newData)
        
        return list(result_dict.values())
