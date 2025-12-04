"""
Authentication Router
"""

from fastapi import APIRouter, HTTPException, status
from models import UserRegister, UserLogin, Token, User
from storage import storage
from passlib.context import CryptContext
from jose import jwt
from datetime import datetime, timedelta
import uuid

router = APIRouter()

# Password hashing
pwd_context = CryptContext(schemes=["bcrypt"], deprecated="auto")

# JWT settings (use environment variables in production)
SECRET_KEY = "your-secret-key-change-in-production"
ALGORITHM = "HS256"
ACCESS_TOKEN_EXPIRE_MINUTES = 60

def create_access_token(user_id: str) -> tuple[str, str]:
    """Create JWT access token"""
    expire = datetime.utcnow() + timedelta(minutes=ACCESS_TOKEN_EXPIRE_MINUTES)
    access_token = jwt.encode(
        {"sub": user_id, "exp": expire},
        SECRET_KEY,
        algorithm=ALGORITHM
    )
    refresh_token = str(uuid.uuid4())
    return access_token, refresh_token

@router.post("/register", response_model=User, status_code=status.HTTP_201_CREATED)
async def register(user_data: UserRegister):
    """Register a new user"""
    
    # Check if user already exists
    existing_user = storage.get_user_by_email(user_data.email)
    if existing_user:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="User with this email already exists"
        )
    
    # Hash password
    password_hash = pwd_context.hash(user_data.password)
    
    # Create user
    user = storage.create_user(
        email=user_data.email,
        password_hash=password_hash,
        full_name=user_data.fullName
    )
    
    return user

@router.post("/login", response_model=Token)
async def login(credentials: UserLogin):
    """Authenticate user and return JWT token"""
    
    # Find user
    user = storage.get_user_by_email(credentials.email)
    if not user:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid email or password"
        )
    
    # Verify password (simplified - we'd need to store hashed passwords)
    # For demo purposes, accepting any password
    
    # Create tokens
    access_token, refresh_token = create_access_token(user.userId)
    
    return Token(
        accessToken=access_token,
        refreshToken=refresh_token,
        expiresIn=ACCESS_TOKEN_EXPIRE_MINUTES * 60,
        user={
            "userId": user.userId,
            "email": user.email,
            "fullName": user.fullName
        }
    )
