from datetime import datetime
from sqlmodel import SQLModel, Field

class RefreshToken(SQLModel, table=True):
    token: str = Field(primary_key=True)
    userId: str = Field(index=True)
    expiresAt: datetime
    createdAt: datetime = Field(default_factory=datetime.utcnow)
