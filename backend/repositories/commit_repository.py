from datetime import datetime
import uuid
from typing import List, Optional
from sqlmodel import Session, select
from database import engine
from entities.commit_entity import Commit
from entities.project_entity import Project
from schemas.element_schema import ElementSnapshot

class CommitRepository:
    def create_commit(
        self,
        project_id: str,
        model_id: str,
        message: str,
        author: str,
        storage_url: str,
        change_type: str = "MOD",
        parent_commit: Optional[str] = None
    ) -> Commit:
        with Session(engine) as session:
            commit_id = str(uuid.uuid4())
            
            commit = Commit(
                commitId=commit_id,
                projectId=project_id,
                model_id=model_id,
                message=message,
                author=author,
                timestamp=datetime.utcnow(),
                parentCommit=parent_commit,
                storageUrl=storage_url,
                changeType=change_type,
                elementCount=0, # These would be parsed from metadata if we had it
                changedElements=0 
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

    def get_latest_commit(self, project_id: str) -> Optional[Commit]:
        with Session(engine) as session:
            statement = select(Commit)\
                .where(Commit.projectId == project_id)\
                .order_by(Commit.timestamp.desc())\
                .limit(1)
            return session.exec(statement).first()

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
