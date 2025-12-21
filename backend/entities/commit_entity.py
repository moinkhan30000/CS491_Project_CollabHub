from datetime import datetime
from typing import Optional
from sqlmodel import SQLModel, Field, Column, JSON
from typing import Dict, Any, Optional

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
    changeType: str = Field(default="MOD") # ADD, MOD, DEL

# 2. Database Table: Inherits Base + adds Primary Key
class Commit(CommitBase, table=True):
    commitId: str = Field(primary_key=True)
    
    # Pointer to Object Storage (S3/Blob) instead of JSON blob
    storageUrl: Optional[str] = None
    snapshot: Optional[Dict[str, Any]] = Field(default=None, sa_column=Column(JSON))
