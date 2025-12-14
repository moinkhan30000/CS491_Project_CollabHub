from typing import List, Optional
from datetime import datetime
import uuid
from sqlmodel import Session, select
from database import engine
from models import User, Project, Commit, ElementSnapshot

class Storage:
    """
    Database-backed storage using SQLModel.
    Replaces the old in-memory dictionaries with actual SQL queries.
    """
    
    # ==========================================
    # USER OPERATIONS
    # ==========================================
    
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
        with Session(engine) as session:
            return session.get(User, user_id)

    # ==========================================
    # PROJECT OPERATIONS
    # ==========================================
    
    def create_project(self, name: str, description: Optional[str], created_by: str) -> Project:
        with Session(engine) as session:
            project = Project(
                projectId=str(uuid.uuid4()),
                name=name,
                description=description,
                createdBy=created_by,
                createdAt=datetime.utcnow(),
                lastModified=datetime.utcnow(),
                memberCount=1
            )
            session.add(project)
            session.commit()
            session.refresh(project)
            return project

    def get_project(self, project_id: str) -> Optional[Project]:
        with Session(engine) as session:
            return session.get(Project, project_id)

    def list_projects(self) -> List[Project]:
        with Session(engine) as session:
            statement = select(Project)
            return session.exec(statement).all()

    # ==========================================
    # SNAPSHOT & COMMIT OPERATIONS
    # ==========================================
    
    def create_commit(
        self,
        project_id: str,
        model_id: str,
        message: str,
        author: str,
        snapshot: ElementSnapshot, # Takes the Pydantic Object
        parent_commit: Optional[str] = None
    ) -> Commit:
        with Session(engine) as session:
            commit_id = str(uuid.uuid4())
            
            # EXPLANATION 1: Serialization
            # We convert the complex Pydantic object (snapshot) into a plain Python Dictionary
            # using .model_dump(). The Database (SQLModel/SQLAlchemy) will automatically 
            # turn this Dict into a JSON string to store it in the "snapshot" column.
            snapshot_data = snapshot.model_dump()
            
            commit = Commit(
                commitId=commit_id,
                projectId=project_id,
                modelId=model_id,
                message=message,
                author=author,
                timestamp=datetime.utcnow(),
                parentCommit=parent_commit,
                snapshot=snapshot_data, # Saved as JSON
                elementCount=len(snapshot.elements),
                changedElements=len(snapshot.elements)
            )
            
            # Update the project's "lastModified" time
            project = session.get(Project, project_id)
            if project:
                project.lastModified = datetime.utcnow()
                session.add(project)
            
            session.add(commit)
            session.commit()
            session.refresh(commit)
            return commit

    def get_commit(self, commit_id: str) -> Optional[Commit]:
        with Session(engine) as session:
            return session.get(Commit, commit_id)

    def get_snapshot(self, commit_id: str) -> Optional[ElementSnapshot]:
        """
        Retrieves the snapshot for a commit.
        Converts the stored JSON back into a Pydantic object.
        """
        with Session(engine) as session:
            commit = session.get(Commit, commit_id)
            
            # EXPLANATION 2: Deserialization
            # The DB gives us back a Dictionary (from the JSON column).
            # We must convert it back into an `ElementSnapshot` object so that 
            # the rest of your code (diff.py, merge.py) can use dot notation (e.g. element.id)
            if commit and commit.snapshot:
                return ElementSnapshot(**commit.snapshot)
            return None

    def list_commits(self, project_id: str, limit: int = 50, offset: int = 0) -> List[Commit]:
        """List commits for a project, newest first"""
        with Session(engine) as session:
            statement = select(Commit)\
                .where(Commit.projectId == project_id)\
                .order_by(Commit.timestamp.desc())\
                .offset(offset)\
                .limit(limit)
            return session.exec(statement).all()

    def get_commit_count(self, project_id: str) -> int:
        with Session(engine) as session:
            statement = select(Commit).where(Commit.projectId == project_id)
            results = session.exec(statement).all()
            return len(results)

# Global storage instance
storage = Storage()