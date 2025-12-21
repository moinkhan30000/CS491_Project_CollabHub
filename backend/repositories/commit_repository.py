from datetime import datetime
import uuid
from typing import List, Optional
from sqlmodel import Session, select
from fastapi.encoders import jsonable_encoder
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
        storage_url: Optional[str] = None,
        change_type: str = "MOD",
        parent_commit: Optional[str] = None
        ,
        snapshot: Optional[ElementSnapshot] = None,
        element_count: Optional[int] = None,
        changed_elements: Optional[int] = None
    ) -> Commit:
        with Session(engine) as session:
            commit_id = str(uuid.uuid4())

            snapshot_data = None
            if snapshot is not None:
                snapshot_data = jsonable_encoder(snapshot)
            
            commit = Commit(
                commitId=commit_id,
                projectId=project_id,
                modelId=model_id,
                message=message,
                author=author,
                timestamp=datetime.utcnow(),
                parentCommit=parent_commit,
                storageUrl=storage_url,
                changeType=change_type,
                elementCount=element_count if element_count is not None else (len(snapshot.elements) if snapshot else 0),
                changedElements=changed_elements if changed_elements is not None else (len(snapshot.elements) if snapshot else 0),
                snapshot=snapshot_data
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

    def get_latest_commit_for_model(self, project_id: str, model_id: Optional[str]) -> Optional[Commit]:
        if not model_id:
            return self.get_latest_commit(project_id)

        with Session(engine) as session:
            statement = select(Commit)\
                .where(Commit.projectId == project_id)\
                .where(Commit.modelId == model_id)\
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

    def get_snapshot(self, commit_id: str) -> Optional[ElementSnapshot]:
        with Session(engine) as session:
            commit = session.get(Commit, commit_id)
            if commit and commit.snapshot:
                return ElementSnapshot(**commit.snapshot)
            return None
