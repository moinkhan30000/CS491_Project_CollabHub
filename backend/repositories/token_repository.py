from sqlmodel import Session, select
from entities.token_entity import RefreshToken
from database import engine
from datetime import datetime

class TokenRepository:
    def __init__(self):
        self.engine = engine

    def create_token(self, token: str, user_id: str, expires_at: datetime) -> RefreshToken:
        with Session(self.engine) as session:
            refresh_token = RefreshToken(
                token=token,
                userId=user_id,
                expiresAt=expires_at
            )
            session.add(refresh_token)
            session.commit()
            session.refresh(refresh_token)
            return refresh_token

    def get_token(self, token: str) -> RefreshToken:
        with Session(self.engine) as session:
            statement = select(RefreshToken).where(RefreshToken.token == token)
            return session.exec(statement).first()

    def delete_token(self, token: str):
        with Session(self.engine) as session:
            statement = select(RefreshToken).where(RefreshToken.token == token)
            token_obj = session.exec(statement).first()
            if token_obj:
                session.delete(token_obj)
                session.commit()
                return True
            return False

    def delete_all_user_tokens(self, user_id: str):
        with Session(self.engine) as session:
            statement = select(RefreshToken).where(RefreshToken.userId == user_id)
            results = session.exec(statement).all()
            for token in results:
                session.delete(token)
            session.commit()
