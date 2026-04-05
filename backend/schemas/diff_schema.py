from pydantic import BaseModel
from typing import Optional, Any, Literal, List, Dict
from datetime import datetime

class ParameterChange(BaseModel):
    name: str
    oldValue: Any
    newValue: Any
    type: str
    elementName: Optional[str] = None

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
