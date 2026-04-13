from copy import deepcopy
from typing import Any, Dict, List, Optional, Tuple

from schemas.diff_schema import Change, ParameterChange
from schemas.element_schema import Element
from schemas.operation_schema import Operation, OpsCommitPayload


class OperationEngine:
    """Build and replay semantic operation payloads for delta commits."""

    def build_payload_from_changes(self, changes: List[Change]) -> OpsCommitPayload:
        operations: List[Operation] = []

        for change in changes or []:
            if change.changeType == "added":
                operations.append(
                    Operation(
                        op="create_native",
                        elementId=change.elementId,
                        category=change.category,
                        type=change.type,
                        newTypeName=self._read_dict_value(change.newData, "typeName"),
                        newFamilyName=self._read_dict_value(change.newData, "familyName"),
                        hostId=self._read_dict_value(change.newData, "hostId"),
                        geometry=self._read_dict_value(change.newData, "geometry"),
                        oldData=change.oldData,
                        newData=change.newData,
                    )
                )
                continue

            if change.changeType == "deleted":
                operations.append(
                    Operation(
                        op="delete",
                        elementId=change.elementId,
                        category=change.category,
                        type=change.type,
                        oldData=change.oldData,
                    )
                )
                continue

            operations.extend(self._build_modified_operations(change))

        return OpsCommitPayload(commitFormat="ops", operations=operations)

    def to_changes(self, payload: OpsCommitPayload) -> List[Change]:
        changes_by_element: Dict[str, Change] = {}
        ordered_ids: List[str] = []

        for op in payload.operations:
            if op.op in {"create_native", "create_by_copy"}:
                if op.elementId not in ordered_ids:
                    ordered_ids.append(op.elementId)
                changes_by_element[op.elementId] = Change(
                    changeType="added",
                    elementId=op.elementId,
                    category=op.category or self._read_dict_value(op.newData, "category") or "Unknown",
                    type=op.type or self._read_dict_value(op.newData, "type") or "Unknown",
                    parameterChanges=[],
                    geometryChanged=False,
                    locationChanged=False,
                    oldData=op.oldData,
                    newData=op.newData,
                )
                continue

            if op.op == "delete":
                if op.elementId not in ordered_ids:
                    ordered_ids.append(op.elementId)
                changes_by_element[op.elementId] = Change(
                    changeType="deleted",
                    elementId=op.elementId,
                    category=op.category or self._read_dict_value(op.oldData, "category") or "Unknown",
                    type=op.type or self._read_dict_value(op.oldData, "type") or "Unknown",
                    parameterChanges=[],
                    geometryChanged=False,
                    locationChanged=False,
                    oldData=op.oldData,
                    newData=None,
                )
                continue

            existing = changes_by_element.get(op.elementId)
            if existing is None or existing.changeType != "modified":
                existing = Change(
                    changeType="modified",
                    elementId=op.elementId,
                    category=op.category or self._read_dict_value(op.newData, "category")
                             or self._read_dict_value(op.oldData, "category") or "Unknown",
                    type=op.type or self._read_dict_value(op.newData, "type")
                         or self._read_dict_value(op.oldData, "type") or "Unknown",
                    parameterChanges=[],
                    geometryChanged=False,
                    locationChanged=False,
                    oldData=op.oldData,
                    newData=op.newData,
                )
                changes_by_element[op.elementId] = existing
                if op.elementId not in ordered_ids:
                    ordered_ids.append(op.elementId)
            else:
                if existing.oldData is None and op.oldData is not None:
                    existing.oldData = op.oldData
                if op.newData is not None:
                    existing.newData = op.newData

            if op.op == "set_parameter":
                existing.parameterChanges.append(
                    ParameterChange(
                        name=op.parameter or "Unknown",
                        oldValue=op.oldValue,
                        newValue=op.newValue,
                        type=op.parameterType or "Unknown",
                        elementName=op.elementName,
                    )
                )
            elif op.op in {"move", "rotate"}:
                existing.locationChanged = True

            if self._geometry_diff(op.oldData, op.newData):
                existing.geometryChanged = True

        return [changes_by_element[element_id] for element_id in ordered_ids]

    def apply_operations(
        self,
        base_elements: List[Element],
        operations: List[Operation],
    ) -> List[Element]:
        result_dict: Dict[str, Dict[str, Any]] = {
            elem.id: elem.model_dump() for elem in base_elements
        }

        for op in operations:
            if op.op in {"create_native", "create_by_copy"}:
                if op.newData:
                    result_dict[op.elementId] = deepcopy(op.newData)
                continue

            if op.op == "delete":
                result_dict.pop(op.elementId, None)
                continue

            current = result_dict.get(op.elementId)
            if current is None:
                if op.newData:
                    result_dict[op.elementId] = deepcopy(op.newData)
                continue

            if op.op == "set_parameter":
                self._apply_parameter_op(current, op)
            elif op.op == "change_type":
                self._apply_type_change_op(current, op)
            elif op.op in {"move", "rotate"}:
                self._apply_location_op(current, op)

            if op.newData is not None:
                current["type"] = op.newData.get("type", current.get("type"))
                current["familyName"] = op.newData.get("familyName", current.get("familyName"))
                current["typeName"] = op.newData.get("typeName", current.get("typeName"))
                current["geometry"] = op.newData.get("geometry", current.get("geometry"))
                current["location"] = op.newData.get("location", current.get("location"))

        return [Element(**data) for data in result_dict.values()]

    def _build_modified_operations(self, change: Change) -> List[Operation]:
        operations: List[Operation] = []
        old_data = change.oldData or {}
        new_data = change.newData or {}

        if self._type_signature(old_data) != self._type_signature(new_data):
            operations.append(
                Operation(
                    op="change_type",
                    elementId=change.elementId,
                    category=change.category,
                    type=change.type,
                    oldTypeName=self._read_dict_value(old_data, "typeName"),
                    newTypeName=self._read_dict_value(new_data, "typeName"),
                    newFamilyName=self._read_dict_value(new_data, "familyName"),
                    oldData=change.oldData,
                    newData=change.newData,
                )
            )

        for param_change in change.parameterChanges or []:
            operations.append(
                Operation(
                    op="set_parameter",
                    elementId=change.elementId,
                    category=change.category,
                    type=change.type,
                    parameter=param_change.name,
                    oldValue=param_change.oldValue,
                    newValue=param_change.newValue,
                    parameterType=param_change.type,
                    elementName=param_change.elementName,
                    oldData=change.oldData,
                    newData=change.newData,
                )
            )

        if change.locationChanged or change.geometryChanged:
            old_location = self._read_dict_value(old_data, "location")
            new_location = self._read_dict_value(new_data, "location")

            operations.append(
                Operation(
                    op="move",
                    elementId=change.elementId,
                    category=change.category,
                    type=change.type,
                    oldLocation=old_location,
                    newLocation=new_location,
                    vector=self._calculate_vector(old_location, new_location),
                    geometry=self._read_dict_value(new_data, "geometry"),
                    oldData=change.oldData,
                    newData=change.newData,
                )
            )

            rotation_delta = self._calculate_rotation_delta(old_location, new_location)
            if rotation_delta is not None and abs(rotation_delta) > 1e-9:
                operations.append(
                    Operation(
                        op="rotate",
                        elementId=change.elementId,
                        category=change.category,
                        type=change.type,
                        oldLocation=old_location,
                        newLocation=new_location,
                        rotationDelta=rotation_delta,
                        oldData=change.oldData,
                        newData=change.newData,
                    )
                )

        return operations

    @staticmethod
    def _read_dict_value(data: Optional[Dict[str, Any]], key: str) -> Any:
        if not data:
            return None
        return data.get(key)

    @staticmethod
    def _type_signature(data: Optional[Dict[str, Any]]) -> Tuple[Any, Any, Any]:
        return (
            OperationEngine._read_dict_value(data, "familyName"),
            OperationEngine._read_dict_value(data, "typeName"),
            OperationEngine._read_dict_value(data, "type"),
        )

    def _apply_parameter_op(self, element: Dict[str, Any], op: Operation) -> None:
        parameters = element.setdefault("parameters", {})
        existing = deepcopy(parameters.get(op.parameter, {}))
        if op.newData:
            new_params = op.newData.get("parameters", {})
            if op.parameter in new_params:
                parameters[op.parameter] = deepcopy(new_params[op.parameter])
                return

        existing["value"] = op.newValue
        if op.parameterType is not None:
            existing["type"] = op.parameterType
        if op.elementName is not None:
            existing["elementName"] = op.elementName
        parameters[op.parameter] = existing

    def _apply_type_change_op(self, element: Dict[str, Any], op: Operation) -> None:
        if op.newFamilyName is not None:
            element["familyName"] = op.newFamilyName
        if op.newTypeName is not None:
            element["typeName"] = op.newTypeName
        if op.newData:
            element["type"] = op.newData.get("type", element.get("type"))

    def _apply_location_op(self, element: Dict[str, Any], op: Operation) -> None:
        if op.newLocation is not None:
            element["location"] = deepcopy(op.newLocation)
        if op.geometry is not None:
            element["geometry"] = deepcopy(op.geometry)

    @staticmethod
    def _calculate_vector(
        old_location: Optional[Dict[str, Any]],
        new_location: Optional[Dict[str, Any]],
    ) -> Optional[List[float]]:
        if not old_location or not new_location:
            return None

        old_type = old_location.get("type")
        new_type = new_location.get("type")
        if old_type != new_type:
            return None

        if old_type == "point":
            old_point = old_location.get("point") or {}
            new_point = new_location.get("point") or {}
            return [
                (new_point.get("x") or 0) - (old_point.get("x") or 0),
                (new_point.get("y") or 0) - (old_point.get("y") or 0),
                (new_point.get("z") or 0) - (old_point.get("z") or 0),
            ]

        if old_type == "curve":
            old_start = old_location.get("startPoint") or {}
            old_end = old_location.get("endPoint") or {}
            new_start = new_location.get("startPoint") or {}
            new_end = new_location.get("endPoint") or {}
            start_delta = [
                (new_start.get("x") or 0) - (old_start.get("x") or 0),
                (new_start.get("y") or 0) - (old_start.get("y") or 0),
                (new_start.get("z") or 0) - (old_start.get("z") or 0),
            ]
            end_delta = [
                (new_end.get("x") or 0) - (old_end.get("x") or 0),
                (new_end.get("y") or 0) - (old_end.get("y") or 0),
                (new_end.get("z") or 0) - (old_end.get("z") or 0),
            ]
            if all(abs(start_delta[i] - end_delta[i]) < 1e-9 for i in range(3)):
                return start_delta

        return None

    @staticmethod
    def _calculate_rotation_delta(
        old_location: Optional[Dict[str, Any]],
        new_location: Optional[Dict[str, Any]],
    ) -> Optional[float]:
        if not old_location or not new_location:
            return None
        if old_location.get("type") != "point" or new_location.get("type") != "point":
            return None
        old_rotation = old_location.get("rotation")
        new_rotation = new_location.get("rotation")
        if old_rotation is None or new_rotation is None:
            return None
        return float(new_rotation) - float(old_rotation)

    @staticmethod
    def _geometry_diff(
        old_data: Optional[Dict[str, Any]],
        new_data: Optional[Dict[str, Any]],
    ) -> bool:
        old_geometry = (old_data or {}).get("geometry")
        new_geometry = (new_data or {}).get("geometry")
        return old_geometry != new_geometry
