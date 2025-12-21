"""
Authentication Dependencies
Shared authentication utilities to avoid circular imports
"""

from fastapi import HTTPException, status, Depends
from fastapi.security import OAuth2PasswordBearer
from jose import JWTError, jwt
import os

from entities.user_entity import User
from repositories.user_repository import UserRepository

# OAuth2 Scheme
oauth2_scheme = OAuth2PasswordBearer(tokenUrl="api/v1/auth/login")

# JWT settings (must match auth.py)
SECRET_KEY = os.getenv("SECRET_KEY", "your-secret-key-change-in-production")
ALGORITHM = "HS256"

user_repo = UserRepository()

async def get_current_user(token: str = Depends(oauth2_scheme)) -> User:
    """Dependency for protected routes - extracts user from JWT token"""
    credentials_exception = HTTPException(
        status_code=status.HTTP_401_UNAUTHORIZED,
        detail="Could not validate credentials",
        headers={"WWW-Authenticate": "Bearer"},
    )
    try:
        payload = jwt.decode(token, SECRET_KEY, algorithms=[ALGORITHM])
        user_id: str = payload.get("sub")
        if user_id is None:
            raise credentials_exception
    except JWTError:
        raise credentials_exception
    
    # JWT 'sub' claim contains userId, so use get_user_by_id
    user = user_repo.get_user_by_id(user_id)
    
    if user is None:
        raise credentials_exception
        
    return user
