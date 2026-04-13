import os
import sys
import unittest


sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "../backend")))

from diff_engine import DiffEngine
from schemas.diff_schema import Change, ParameterChange
from schemas.element_schema import Element, Geometry, Location, Parameter, Point3D
from services.operation_engine import OperationEngine


class DiffAndOperationEngineTests(unittest.TestCase):
    def setUp(self):
        self.diff_engine = DiffEngine()
        self.operation_engine = OperationEngine()

    def test_diff_preserves_element_name_for_element_id_parameter_changes(self):
        base_element = self._build_element(
            type_name="Generic - 200mm",
            type_value="111",
            element_name="Basic Wall : Generic - 200mm",
            x=0.0,
            geometry_hash="oldhash",
        )
        target_element = self._build_element(
            type_name="Concrete - 300mm",
            type_value="222",
            element_name="Basic Wall : Concrete - 300mm",
            x=1.0,
            geometry_hash="newhash",
        )

        diff_result = self.diff_engine.compute_diff(
            base_elements=[base_element],
            target_elements=[target_element],
            base_version="base",
            target_version="target",
        )

        self.assertEqual(1, len(diff_result.changes))
        change = diff_result.changes[0]
        self.assertEqual("modified", change.changeType)
        self.assertEqual(1, len(change.parameterChanges))
        self.assertEqual(
            "Basic Wall : Concrete - 300mm",
            change.parameterChanges[0].elementName,
        )

    def test_operation_payload_roundtrip_keeps_semantic_type_and_parameter_data(self):
        change = self._build_modified_change()

        payload = self.operation_engine.build_payload_from_changes([change])

        self.assertEqual("ops", payload.commitFormat)
        self.assertIn("change_type", [op.op for op in payload.operations])
        self.assertIn("set_parameter", [op.op for op in payload.operations])
        self.assertIn("move", [op.op for op in payload.operations])

        roundtrip_changes = self.operation_engine.to_changes(payload)

        self.assertEqual(1, len(roundtrip_changes))
        roundtrip_change = roundtrip_changes[0]
        self.assertEqual("modified", roundtrip_change.changeType)
        self.assertTrue(roundtrip_change.locationChanged)
        self.assertEqual(
            "Basic Wall : Concrete - 300mm",
            roundtrip_change.parameterChanges[0].elementName,
        )
        self.assertEqual(
            "Concrete - 300mm",
            roundtrip_change.newData["typeName"],
        )

    def test_apply_operations_reconstructs_modified_element_state(self):
        base_element = self._build_element(
            type_name="Generic - 200mm",
            type_value="111",
            element_name="Basic Wall : Generic - 200mm",
            x=0.0,
            geometry_hash="oldhash",
        )
        payload = self.operation_engine.build_payload_from_changes([self._build_modified_change()])

        updated_elements = self.operation_engine.apply_operations(
            base_elements=[base_element],
            operations=payload.operations,
        )

        self.assertEqual(1, len(updated_elements))
        updated = updated_elements[0]
        self.assertEqual("Concrete - 300mm", updated.typeName)
        self.assertEqual("Basic Wall", updated.familyName)
        self.assertEqual("222", updated.parameters["Type"].value)
        self.assertEqual(
            "Basic Wall : Concrete - 300mm",
            updated.parameters["Type"].elementName,
        )
        self.assertEqual(1.0, updated.location.point.x)
        self.assertEqual("newhash", updated.geometry.geometryHash)

    def _build_modified_change(self) -> Change:
        base_element = self._build_element(
            type_name="Generic - 200mm",
            type_value="111",
            element_name="Basic Wall : Generic - 200mm",
            x=0.0,
            geometry_hash="oldhash",
        )
        target_element = self._build_element(
            type_name="Concrete - 300mm",
            type_value="222",
            element_name="Basic Wall : Concrete - 300mm",
            x=1.0,
            geometry_hash="newhash",
        )

        return Change(
            changeType="modified",
            elementId=base_element.id,
            category=base_element.category,
            type=base_element.type,
            parameterChanges=[
                ParameterChange(
                    name="Type",
                    oldValue="111",
                    newValue="222",
                    type="ElementId",
                    elementName="Basic Wall : Concrete - 300mm",
                )
            ],
            geometryChanged=True,
            locationChanged=True,
            oldData=base_element.model_dump(),
            newData=target_element.model_dump(),
        )

    @staticmethod
    def _build_element(
        type_name: str,
        type_value: str,
        element_name: str,
        x: float,
        geometry_hash: str,
    ) -> Element:
        return Element(
            id="uid-1",
            category="Walls",
            type=f"Basic Wall: {type_name}",
            familyName="Basic Wall",
            typeName=type_name,
            parameters={
                "Type": Parameter(
                    value=type_value,
                    type="ElementId",
                    isReadOnly=False,
                    storageType="ElementId",
                    elementName=element_name,
                )
            },
            geometry=Geometry(geometryHash=geometry_hash),
            location=Location(
                type="point",
                point=Point3D(x=x, y=0.0, z=0.0),
                rotation=0.0,
            ),
        )


if __name__ == "__main__":
    unittest.main()
