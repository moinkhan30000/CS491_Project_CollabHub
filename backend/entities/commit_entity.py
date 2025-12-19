from datetime import datetime
from typing import Optional, Dict, Any, List
from sqlmodel import SQLModel, Field, JSON, Column

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
