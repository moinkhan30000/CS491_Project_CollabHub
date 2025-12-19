from datetime import datetime
import uuid
from typing import List, Optional
from sqlmodel import Session, select
from database import engine
from entities.commit_entity import Commit
from entities.project_entity import Project
from models import ElementSnapshot

class CommitRepository:
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
                changedElements=len(snapshot.elements) # This logic might be simplified, assuming all are changes for now? Or caller logic handles diffs? The original code had this.
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
