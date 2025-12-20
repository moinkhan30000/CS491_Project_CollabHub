from pydantic import BaseModel
from typing import Optional, List, Dict
from datetime import datetime
from schemas.element_schema import ElementSnapshot

class CommitCreate(BaseModel):
    modelId: str
    commitMessage: str
    parentCommit: Optional[str] = None
    snapshot: ElementSnapshot

class CommitDetail(BaseModel):
    # Field duplicated from CommitBase to decrypt dependency
    projectId: str
    modelId: str
    message: str
    author: str
    timestamp: datetime
    parentCommit: Optional[str] = None
    elementCount: int
    changedElements: int
    
    commitId: str
    children: List[str] = []
    summary: Dict[str, int]
