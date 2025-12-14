import os
from sqlmodel import SQLModel, create_engine, Session
from dotenv import load_dotenv

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
# Note: We removed {"check_same_thread": False} because that is only for SQLite
engine = create_engine(DATABASE_URL, echo=True)

def create_db_and_tables():
    """
    Create tables if they don't exist.
    SQLModel checks your models.py and generates the SQL automatically.
    """
    SQLModel.metadata.create_all(engine)

def get_session():
    with Session(engine) as session:
        yield session