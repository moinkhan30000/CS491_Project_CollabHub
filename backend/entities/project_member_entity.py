from datetime import datetime
from typing import Optional
from sqlmodel import SQLModel, Field

class ProjectMember(SQLModel, table=True):
    id: Optional[int] = Field(default=None, primary_key=True)
    projectId: str = Field(foreign_key="project.projectId", index=True)
    userId: str = Field(foreign_key="user.userId", index=True)
    role: str = Field(default="COLLABORATOR") # OWNER, COLLABORATOR
    status: str = Field(default="PENDING") # PENDING, ACTIVE, DECLINED
    invitedAt: datetime = Field(default_factory=datetime.utcnow)
