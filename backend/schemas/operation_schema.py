from pydantic import BaseModel, Field
from typing import Optional, Any, Literal, List, Dict


class PayloadRef(BaseModel):
    payloadId: Optional[str] = None
    storageUrl: Optional[str] = None
    contentHash: Optional[str] = None
    categories: List[str] = Field(default_factory=list)
    markers: List[str] = Field(default_factory=list)


class MappingUpdate(BaseModel):
    repoGuid: Optional[str] = None
    elementId: Optional[str] = None
    uniqueId: Optional[str] = None
    status: Literal["active", "deleted", "recreated", "missing"] = "active"


class Operation(BaseModel):
    op: Literal[
        "set_parameter",
        "move",
        "rotate",
        "change_type",
        "delete",
        "create_native",
        "create_by_copy",
    ]
    elementId: str
    repoGuid: Optional[str] = None
    category: Optional[str] = None
    type: Optional[str] = None
    parameter: Optional[str] = None
    oldValue: Any = None
    newValue: Any = None
    parameterType: Optional[str] = None
    elementName: Optional[str] = None
    oldLocation: Optional[Dict[str, Any]] = None
    newLocation: Optional[Dict[str, Any]] = None
    vector: Optional[List[float]] = None
    rotationDelta: Optional[float] = None
    oldTypeName: Optional[str] = None
    newTypeName: Optional[str] = None
    newFamilyName: Optional[str] = None
    hostId: Optional[str] = None
    geometry: Optional[Dict[str, Any]] = None
    payload: Optional[str] = None
    marker: Optional[str] = None
    oldData: Optional[Dict[str, Any]] = None
    newData: Optional[Dict[str, Any]] = None


class OpsCommitPayload(BaseModel):
    commitFormat: Literal["ops"] = "ops"
    operations: List[Operation] = Field(default_factory=list)
    payloadRefs: List[PayloadRef] = Field(default_factory=list)
    mappingUpdates: List[MappingUpdate] = Field(default_factory=list)
