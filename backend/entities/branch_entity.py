from datetime import datetime
from typing import Optional
from sqlmodel import SQLModel, Field

class Branch(SQLModel, table=True):
    branchId: str = Field(primary_key=True)
    projectId: str = Field(foreign_key="project.projectId", index=True)
    name: str = Field(index=True)
    headCommitId: Optional[str] = Field(default=None, foreign_key="commit.commitId")
    createdAt: datetime = Field(default_factory=datetime.utcnow)
    createdBy: str = Field(foreign_key="user.userId")
