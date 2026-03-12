from datetime import datetime
from typing import Optional, Dict, Any
from sqlmodel import SQLModel, Field, Column, JSON

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
    changeType: str = Field(default="MOD")  # ADD, MOD, DEL

# 2. Database Table: Inherits Base + adds Primary Key
class Commit(CommitBase, table=True):
    commitId: str = Field(primary_key=True)

    storageUrl: Optional[str] = None

    # True  :snapshot column holds a full ElementSnapshot (root commit)
    # False :snapshot column holds List[Change] (delta commit)
    isFullSnapshot: bool = Field(default=True)

    # Holds either ElementSnapshot JSON or List[Change] JSON depending on isFullSnapshot
    snapshot: Optional[Dict[str, Any]] = Field(default=None, sa_column=Column(JSON))
