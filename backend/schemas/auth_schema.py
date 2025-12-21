from pydantic import BaseModel, Field, EmailStr
from typing import Dict, Any
from datetime import datetime

class UserRegister(BaseModel):
    email: EmailStr
    password: str = Field(min_length=8)
    fullName: str

class UserLogin(BaseModel):
    email: EmailStr
    password: str

class Token(BaseModel):
    accessToken: str
    refreshToken: str
    expiresIn: int
    user: Dict[str, Any]

class UserRead(BaseModel):
    userId: str
    email: str
    fullName: str
    createdAt: datetime
