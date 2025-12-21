"""
Pydantic models for request/response validation
"""

from pydantic import BaseModel, Field, EmailStr
from typing import List, Dict, Optional, Any, Literal
from datetime import datetime
from sqlmodel import SQLModel, Field, JSON, Column

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

class User(SQLModel, table=True):
    userId: str = Field(primary_key=True)
    email: str = Field(index=True, unique=True) # Changed to str for DB compatibility
    password_hash: str
    fullName: str
    createdAt: datetime = Field(default_factory=datetime.utcnow)

# ============= Project Models =============

class ProjectCreate(BaseModel):
    name: str
    description: Optional[str] = None
    settings: Optional[Dict[str, Any]] = None

class Project(SQLModel, table=True):
    projectId: str = Field(primary_key=True)
    name: str
    description: Optional[str] = None
    createdBy: str = Field(foreign_key="user.userId")
    createdAt: datetime = Field(default_factory=datetime.utcnow)
    lastModified: datetime = Field(default_factory=datetime.utcnow)
    memberCount: int = 0
    # Store settings as JSON in the database
    settings: Optional[Dict[str, Any]] = Field(default=None, sa_column=Column(JSON))

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

# ============= Commit Models =============

# 1. Base Class: Shared fields (No table=True here)
class CommitBase(SQLModel):
    projectId: str = Field(foreign_key="project.projectId", index=True)
    modelId: str
    message: str
    author: str
    timestamp: datetime = Field(default_factory=datetime.utcnow)
    parentCommit: Optional[str] = Field(default=None, foreign_key="commit.commitId")
    elementCount: int
    changedElements: int

# 2. Database Table: Inherits Base + adds Primary Key and JSON Column
class Commit(CommitBase, table=True):
    commitId: str = Field(primary_key=True)
    
    # The JSON Blob column (Only exists in the DB version)
    snapshot: Dict[str, Any] = Field(default={}, sa_column=Column(JSON))

# 3. Request Model: What the user sends to create a commit
class CommitCreate(BaseModel):
    modelId: str
    commitMessage: str
    parentCommit: Optional[str] = None
    snapshot: ElementSnapshot

# 4. Response/Detail Model: Inherits Base (NOT Table) + adds extra fields
class CommitDetail(CommitBase):
    commitId: str
    children: List[str] = []
    summary: Dict[str, int]
    # We deliberately exclude 'snapshot' here to keep the response light

class CommitSummary(CommitBase):
    commitId: str

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
