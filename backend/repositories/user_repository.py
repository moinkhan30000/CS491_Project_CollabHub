from datetime import datetime
import uuid
from typing import Optional
from sqlmodel import Session, select
from database import engine
from entities.user_entity import User

class UserRepository:
    def create_user(self, email: str, password_hash: str, full_name: str) -> User:
        """Create a new user in the database"""
        with Session(engine) as session:
            user = User(
                userId=str(uuid.uuid4()),
                email=email,
                password_hash=password_hash,
                fullName=full_name,
                createdAt=datetime.utcnow()
            )
            session.add(user)
            session.commit()
            session.refresh(user) # Reloads the object with DB data
            return user
    
    def get_user_by_email(self, email: str) -> Optional[User]:
        """Find a user by email using a SQL SELECT"""
        with Session(engine) as session:
            # Equivalent to: SELECT * FROM user WHERE email = '...'
            statement = select(User).where(User.email == email)
            return session.exec(statement).first()

    def get_user_by_id(self, user_id: str) -> Optional[User]:
        """Find a user by ID"""
        with Session(engine) as session:
            return session.get(User, user_id)

