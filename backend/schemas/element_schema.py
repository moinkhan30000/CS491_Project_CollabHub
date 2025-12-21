from pydantic import BaseModel
from typing import List, Dict, Optional, Any, Literal
from datetime import datetime

class Point3D(BaseModel):
    x: float
    y: float
    z: float

class Parameter(BaseModel):
    value: Any
    type: str
    isReadOnly: bool = False
    storageType: str

class BoundingBox(BaseModel):
    min: Point3D
    max: Point3D

class Geometry(BaseModel):
    boundingBox: Optional[BoundingBox] = None
    geometryHash: Optional[str] = None
    solids: Optional[List[Dict[str, float]]] = None

class Location(BaseModel):
    type: Literal["point", "curve", "transform"]
    point: Optional[Point3D] = None
    rotation: Optional[float] = None
    startPoint: Optional[Point3D] = None
    endPoint: Optional[Point3D] = None
    origin: Optional[Point3D] = None
    basisX: Optional[Point3D] = None
    basisY: Optional[Point3D] = None
    basisZ: Optional[Point3D] = None

class Element(BaseModel):
    id: str  # Revit UniqueId
    category: str
    type: str
    familyName: Optional[str] = None
    typeName: Optional[str] = None
    parameters: Dict[str, Parameter]
    geometry: Optional[Geometry] = None
    location: Optional[Location] = None
    worksetId: Optional[str] = None
    levelId: Optional[str] = None
    phaseCreated: Optional[str] = None
    phaseDemolished: Optional[str] = None

class ElementSnapshot(BaseModel):
    version: str = "1.0"
    projectId: str
    modelId: str
    timestamp: datetime
    userName: str
    commitMessage: Optional[str] = None
    elements: List[Element]
    metadata: Optional[Dict[str, Any]] = None
