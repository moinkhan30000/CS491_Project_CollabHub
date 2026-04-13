from pydantic import BaseModel, Field
from typing import Optional, List, Dict, Any, Union
from datetime import datetime
from schemas.element_schema import ElementSnapshot
from schemas.diff_schema import Change
from schemas.operation_schema import PayloadRef

class CommitCreate(BaseModel):
    modelId: str
    commitMessage: str
    parentCommit: Optional[str] = None
    snapshot: ElementSnapshot


class CommitPackageCreate(BaseModel):
    modelId: str
    commitMessage: str
    parentCommit: str
    changes: List[Change]
    elementCount: int
    payloadRefs: List[PayloadRef] = Field(default_factory=list)
    checkpointSnapshot: Optional[ElementSnapshot] = None

class AuthorInfo(BaseModel):
    userId: str
    fullName: Optional[str] = None

class CommitDetail(BaseModel):
    # Field duplicated from CommitBase to decrypt dependency
    projectId: str
    modelId: str
    message: str
    author: Union[AuthorInfo, str, Dict[str, Any]]
    timestamp: datetime
    parentCommit: Optional[str] = None
    elementCount: int
    changedElements: int
    
    commitId: str
    children: List[str] = []
    summary: Dict[str, int]

class CommitSummary(BaseModel):
    projectId: str
    modelId: str
    message: str
    author: Union[AuthorInfo, str, Dict[str, Any]]
    timestamp: datetime
    parentCommit: Optional[str] = None
    elementCount: int
    changedElements: int
    commitId: str
