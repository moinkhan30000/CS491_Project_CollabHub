import uuid
import os
from sqlmodel import SQLModel, create_engine, Session, select
from dotenv import load_dotenv
from passlib.context import CryptContext

from entities.user_entity import User

# Load .env file
load_dotenv()

# Get DB credentials
user = os.getenv("POSTGRES_USER", "postgres")
password = os.getenv("POSTGRES_PASSWORD", "password")
host = os.getenv("DB_HOST", "localhost")
db_name = os.getenv("POSTGRES_DB", "revit_vcs")
port = os.getenv("DB_PORT", "5432")

# Construct the PostgreSQL Connection URL
DATABASE_URL = f"postgresql://{user}:{password}@{host}:{port}/{db_name}"

# Create Engine
engine = create_engine(DATABASE_URL, echo=True)
pwd_context = CryptContext(schemes=["pbkdf2_sha256"], deprecated="auto")

DEV_TEST_USERS = (
    ("user1@gmail.com", "User1"),
    ("user2@gmail.com", "User2"),
)

def create_db_and_tables():
    """
    Create tables if they don't exist.
    SQLModel checks your models.py and generates the SQL automatically.
    """
    SQLModel.metadata.create_all(engine)


def seed_dev_test_users():
    """
    Seed deterministic local test users for faster manual testing.
    Remove this before final delivery/production use.
    """
    with Session(engine) as session:
        for email, full_name in DEV_TEST_USERS:
            existing = session.exec(select(User).where(User.email == email)).first()
            if existing is not None:
                continue

            session.add(
                User(
                    userId=str(uuid.uuid4()),
                    email=email,
                    password_hash=pwd_context.hash("password123"),
                    fullName=full_name,
                )
            )

        session.commit()

def get_session():
    with Session(engine) as session:
        yield session
