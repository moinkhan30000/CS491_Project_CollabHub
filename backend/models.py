"""
Pydantic models for request/response validation
"""

from pydantic import BaseModel, Field, EmailStr
from typing import List, Dict, Optional, Any, Literal
from datetime import datetime

# ============= Authentication Models =============

class UserRegister(BaseModel):
    email: EmailStr
    password: str = Field(min_length=8)
    fullName: str

class UserLogin(BaseModel):
    email: EmailStr
    password: str

class Token(BaseModel):
    accessToken: str
    refreshToken: str
    expiresIn: int
    user: Dict[str, Any]

class User(BaseModel):
    userId: str
    email: EmailStr
    fullName: str
    createdAt: datetime

# ============= Project Models =============

class ProjectCreate(BaseModel):
    name: str
    description: Optional[str] = None
    settings: Optional[Dict[str, Any]] = None

class Project(BaseModel):
    projectId: str
    name: str
    description: Optional[str] = None
    createdBy: str
    createdAt: datetime
    lastModified: datetime
    memberCount: int = 0

# ============= Element & Snapshot Models =============

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
    type: Literal["point", "curve"]
    point: Optional[Point3D] = None
    rotation: Optional[float] = None
    startPoint: Optional[Point3D] = None
    endPoint: Optional[Point3D] = None

class Element(BaseModel):
    id: str  # Revit UniqueId
    category: str
    type: str
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

# ============= Commit Models =============

class CommitCreate(BaseModel):
    modelId: str
    commitMessage: str
    parentCommit: Optional[str] = None
    snapshot: ElementSnapshot

class Commit(BaseModel):
    commitId: str
    projectId: str
    modelId: str
    message: str
    author: str
    timestamp: datetime
    parentCommit: Optional[str] = None
    elementCount: int
    changedElements: int

class CommitDetail(Commit):
    children: List[str] = []
    summary: Dict[str, int]

# ============= Diff Models =============

class ParameterChange(BaseModel):
    name: str
    oldValue: Any
    newValue: Any
    type: str

class Change(BaseModel):
    changeType: Literal["added", "modified", "deleted"]
    elementId: str
    category: str
    type: str
    parameterChanges: List[ParameterChange] = []
    geometryChanged: bool = False
    locationChanged: bool = False
    oldData: Optional[Dict[str, Any]] = None
    newData: Optional[Dict[str, Any]] = None

class Conflict(BaseModel):
    elementId: str
    conflictType: Literal["concurrent_modification", "delete_modified", "parameter_conflict"]
    description: str
    localChange: Optional[Dict[str, Any]] = None
    remoteChange: Optional[Dict[str, Any]] = None
    resolutionOptions: List[Literal["keep_local", "accept_remote", "manual_resolve"]]

class DiffResult(BaseModel):
    baseVersion: str
    targetVersion: str
    timestamp: datetime
    summary: Dict[str, int]
    changes: List[Change]
    conflicts: List[Conflict] = []

# ============= Merge Models =============

class Resolution(BaseModel):
    elementId: str
    resolution: Literal["keep_local", "accept_remote", "manual_resolve"]
    customData: Optional[Dict[str, Any]] = None

class MergeRequest(BaseModel):
    baseCommit: str
    sourceCommit: str
    targetCommit: str
    resolutions: List[Resolution] = []
    message: str

class MergeResult(BaseModel):
    mergeCommitId: str
    status: Literal["success", "conflict", "error"]
    appliedChanges: int
    skippedChanges: int
    conflicts: List[Conflict] = []

class PullRequest(BaseModel):
    currentCommit: str
    targetCommit: str
    strategy: Literal["auto", "manual"] = "auto"
    selectiveElements: Optional[List[str]] = None

class PullResult(BaseModel):
    changes: List[Change]
    conflicts: List[Conflict]
    requiresResolution: bool
