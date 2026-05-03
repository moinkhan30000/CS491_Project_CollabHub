from typing import List, Dict, Any, Tuple
from schemas.diff_schema import Conflict, Resolution
from schemas.element_schema import Element, ElementSnapshot

class HistoricalDecisionService:
    def reconstruct_decisions(self, conflicts: List[Conflict], final_snapshot: ElementSnapshot) -> List[Resolution]:
        resolutions = []
        
        final_elements_by_id = {e.id: e for e in final_snapshot.elements}
        
        for conflict in conflicts:
            resolution_type = "manual_resolve"
            
            if conflict.conflictType == "delete_modified":
                # For delete_modified, localChange and remoteChange will have different changeTypes.
                # One is 'deleted', one is 'modified'.
                is_local_deleted = conflict.localChange and conflict.localChange.get("changeType") == "deleted"
                
                # If the element exists in the final snapshot, it means the 'modified' change was kept.
                # If it doesn't exist, the 'deleted' change was kept.
                if conflict.elementId in final_elements_by_id:
                    # Modified version kept
                    resolution_type = "accept_remote" if is_local_deleted else "keep_local"
                else:
                    # Deleted version kept
                    resolution_type = "keep_local" if is_local_deleted else "accept_remote"
                    
            elif conflict.conflictType == "parameter_conflict" or conflict.conflictType == "concurrent_modification":
                # Both are modified. The final snapshot will have the element.
                # We need to compare parameters.
                final_element = final_elements_by_id.get(conflict.elementId)
                if final_element:
                    local_new_data = conflict.localChange.get("newData", {}) if conflict.localChange else {}
                    remote_new_data = conflict.remoteChange.get("newData", {}) if conflict.remoteChange else {}
                    
                    # A simplistic heuristic: compare the JSON dumps or a specific parameter
                    # If we check the conflicting parameters, we can see which one matches.
                    local_match_count = 0
                    remote_match_count = 0
                    
                    if conflict.conflictingParams:
                        for param_name in conflict.conflictingParams:
                            final_val = final_element.parameters.get(param_name)
                            
                            local_params = local_new_data.get("parameters", {})
                            remote_params = remote_new_data.get("parameters", {})
                            
                            local_val = local_params.get(param_name)
                            remote_val = remote_params.get(param_name)
                            
                            if final_val and local_val and final_val.get("value") == local_val.get("value"):
                                local_match_count += 1
                            if final_val and remote_val and final_val.get("value") == remote_val.get("value"):
                                remote_match_count += 1
                                
                        if local_match_count > remote_match_count:
                            resolution_type = "keep_local"
                        elif remote_match_count > local_match_count:
                            resolution_type = "accept_remote"
                        else:
                            # Tie or manual resolve
                            resolution_type = "keep_local" # Default fallback
                    else:
                        # Fallback if no conflictingParams listed
                        if local_new_data and remote_new_data:
                            resolution_type = "keep_local"
                else:
                    resolution_type = "manual_resolve"
                    
            elif conflict.conflictType == "spatial_collision":
                # Both are added. conflict.elementId is `src_ident|tgt_ident`.
                parts = conflict.elementId.split("|")
                if len(parts) == 2:
                    src_ident = parts[0]
                    tgt_ident = parts[1]
                    
                    has_src = src_ident in final_elements_by_id
                    has_tgt = tgt_ident in final_elements_by_id
                    
                    if has_src and has_tgt:
                        resolution_type = "keep_both"
                    elif has_src:
                        resolution_type = "keep_local"
                    elif has_tgt:
                        resolution_type = "accept_remote"
                    else:
                        resolution_type = "manual_resolve"
                        
            resolutions.append(Resolution(
                elementId=conflict.elementId,
                resolution=resolution_type
            ))
            
        return resolutions
