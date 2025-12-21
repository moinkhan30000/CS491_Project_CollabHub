from datetime import datetime
from typing import Optional, Dict, Any
from sqlmodel import SQLModel, Field, JSON, Column

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
