from datetime import datetime
import uuid
from typing import List, Optional
from sqlmodel import Session, select
from database import engine
from entities.project_entity import Project

class ProjectRepository:
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
