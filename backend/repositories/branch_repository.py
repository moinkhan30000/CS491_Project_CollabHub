import uuid
from typing import List, Optional
from sqlmodel import Session, select
from datetime import datetime

from database import engine
from entities.branch_entity import Branch
from entities.commit_entity import Commit

class BranchRepository:
    def create_branch(self, project_id: str, name: str, user_id: str, head_commit_id: Optional[str] = None) -> Branch:
        with Session(engine) as session:
            branch_id = str(uuid.uuid4())
            branch = Branch(
                branchId=branch_id,
                projectId=project_id,
                name=name,
                headCommitId=head_commit_id,
                createdAt=datetime.utcnow(),
                createdBy=user_id,
            )
            session.add(branch)
            session.commit()
            session.refresh(branch)
            return branch

    def get_branch(self, project_id: str, branch_name: str) -> Optional[Branch]:
        with Session(engine) as session:
            statement = select(Branch).where(Branch.projectId == project_id, Branch.name == branch_name)
            branch = session.exec(statement).first()
            
            # Lazy init for existing projects without branches
            if branch is None and branch_name == "main":
                # Check if there are any commits for the project
                latest_commit = session.exec(
                    select(Commit)
                    .where(Commit.projectId == project_id)
                    .order_by(Commit.timestamp.desc())
                    .limit(1)
                ).first()
                if latest_commit:
                    branch = self.create_branch(
                        project_id=project_id,
                        name="main",
                        user_id=latest_commit.author,
                        head_commit_id=latest_commit.commitId
                    )
            
            return branch

    def get_project_branches(self, project_id: str) -> List[Branch]:
        with Session(engine) as session:
            statement = select(Branch).where(Branch.projectId == project_id).order_by(Branch.name)
            branches = session.exec(statement).all()
            
            # Lazy init "main" for backward compatibility if no branches exist
            if not branches:
                main_branch = self.get_branch(project_id, "main")
                if main_branch:
                    branches = [main_branch]
                    
            return list(branches)

    def update_branch_head(self, branch_id: str, new_head_commit_id: str) -> Optional[Branch]:
        with Session(engine) as session:
            branch = session.get(Branch, branch_id)
            if branch:
                branch.headCommitId = new_head_commit_id
                session.add(branch)
                session.commit()
                session.refresh(branch)
            return branch
