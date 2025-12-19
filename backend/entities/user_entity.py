from datetime import datetime
from sqlmodel import SQLModel, Field

class User(SQLModel, table=True):
    userId: str = Field(primary_key=True)
    email: str = Field(index=True, unique=True)
    password_hash: str
    fullName: str
    createdAt: datetime = Field(default_factory=datetime.utcnow)
