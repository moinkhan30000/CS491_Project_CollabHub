from datetime import datetime
from typing import Optional
from pydantic import BaseModel

class BranchCreate(BaseModel):
    name: str
    headCommitId: Optional[str] = None

class BranchDetail(BaseModel):
    branchId: str
    projectId: str
    name: str
    headCommitId: Optional[str]
    createdAt: datetime
    createdBy: str
