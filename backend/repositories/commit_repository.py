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
    
def get_changes_between_snapshots(
    self,
    from_commit_id: str,
    to_commit_id: str,
    diff_engine
) -> Optional[List]:
    """
    Get the list of changes needed to go from one snapshot to another.
    
    This uses the diff engine to compare snapshots.
    
    Args:
        from_commit_id: Starting commit ID (user has this version)
        to_commit_id: Target commit ID (want this version)
        diff_engine: DiffEngine instance to compute diffs
        
    Returns:
        List of Change objects to apply, or None if commits not found
        
    Example:
        changes = repo.get_changes_between_snapshots(
            from_commit_id="commit-abc",
            to_commit_id="commit-xyz",
            diff_engine=engine
        )
        # Returns: [Change(added, wall-4), Change(deleted, wall-2), ...]
    """
    # Get both snapshots
    from_snapshot = self.get_snapshot(from_commit_id)
    to_snapshot = self.get_snapshot(to_commit_id)
    
    # Validate
    if not from_snapshot:
        print(f"Error: Snapshot for commit {from_commit_id} not found")
        return None
    
    if not to_snapshot:
        print(f"Error: Snapshot for commit {to_commit_id} not found")
        return None
    
    # Compute diff
    diff_result = diff_engine.compute_diff(
        base_elements=from_snapshot.elements,
        target_elements=to_snapshot.elements,
        base_version=from_commit_id,
        target_version=to_commit_id
    )
    
    # Return the changes
    return diff_result.changes