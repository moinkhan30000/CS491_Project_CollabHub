"""
In-memory storage for snapshots and commits
Production version would use a database (PostgreSQL, MongoDB, etc.)
"""

from typing import Dict, List, Optional
from models import ElementSnapshot, Commit, Project, User
from datetime import datetime
import uuid

class Storage:
    """In-memory storage for development/testing"""
    
    def __init__(self):
        # Storage dictionaries
        self.users: Dict[str, User] = {}
        self.projects: Dict[str, Project] = {}
        self.snapshots: Dict[str, ElementSnapshot] = {}  # commitId -> snapshot
        self.commits: Dict[str, Commit] = {}  # commitId -> commit metadata
        self.project_commits: Dict[str, List[str]] = {}  # projectId -> [commitIds]
        
        # Auth tokens (simplified)
        self.tokens: Dict[str, str] = {}  # token -> userId
    
    # ========== User Operations ==========
    
    def create_user(self, email: str, password_hash: str, full_name: str) -> User:
        """Create a new user"""
        user_id = str(uuid.uuid4())
        user = User(
            userId=user_id,
            email=email,
            fullName=full_name,
            createdAt=datetime.utcnow()
        )
        self.users[user_id] = user
        return user
    
    def get_user_by_email(self, email: str) -> Optional[User]:
        """Find user by email"""
        for user in self.users.values():
            if user.email == email:
                return user
        return None
    
    def get_user_by_id(self, user_id: str) -> Optional[User]:
        """Get user by ID"""
        return self.users.get(user_id)
    
    # ========== Project Operations ==========
    
    def create_project(self, name: str, description: Optional[str], created_by: str) -> Project:
        """Create a new project"""
        project_id = str(uuid.uuid4())
        project = Project(
            projectId=project_id,
            name=name,
            description=description,
            createdBy=created_by,
            createdAt=datetime.utcnow(),
            lastModified=datetime.utcnow(),
            memberCount=1
        )
        self.projects[project_id] = project
        self.project_commits[project_id] = []
        return project
    
    def get_project(self, project_id: str) -> Optional[Project]:
        """Get project by ID"""
        return self.projects.get(project_id)
    
    def list_projects(self) -> List[Project]:
        """List all projects"""
        return list(self.projects.values())
    
    # ========== Snapshot & Commit Operations ==========
    
    def create_commit(
        self,
        project_id: str,
        model_id: str,
        message: str,
        author: str,
        snapshot: ElementSnapshot,
        parent_commit: Optional[str] = None
    ) -> Commit:
        """Create a new commit with snapshot"""
        commit_id = str(uuid.uuid4())
        
        # Store snapshot
        self.snapshots[commit_id] = snapshot
        
        # Create commit metadata
        commit = Commit(
            commitId=commit_id,
            projectId=project_id,
            modelId=model_id,
            message=message,
            author=author,
            timestamp=datetime.utcnow(),
            parentCommit=parent_commit,
            elementCount=len(snapshot.elements),
            changedElements=len(snapshot.elements)  # Simplified
        )
        
        self.commits[commit_id] = commit
        
        # Add to project commits
        if project_id in self.project_commits:
            self.project_commits[project_id].append(commit_id)
        else:
            self.project_commits[project_id] = [commit_id]
        
        # Update project last modified
        if project_id in self.projects:
            self.projects[project_id].lastModified = datetime.utcnow()
        
        return commit
    
    def get_commit(self, commit_id: str) -> Optional[Commit]:
        """Get commit metadata"""
        return self.commits.get(commit_id)
    
    def get_snapshot(self, commit_id: str) -> Optional[ElementSnapshot]:
        """Get full snapshot for a commit"""
        return self.snapshots.get(commit_id)
    
    def list_commits(self, project_id: str, limit: int = 50, offset: int = 0) -> List[Commit]:
        """List commits for a project"""
        commit_ids = self.project_commits.get(project_id, [])
        
        # Reverse to get newest first
        commit_ids = list(reversed(commit_ids))
        
        # Apply pagination
        paginated_ids = commit_ids[offset:offset + limit]
        
        return [self.commits[cid] for cid in paginated_ids if cid in self.commits]
    
    def get_commit_count(self, project_id: str) -> int:
        """Get total commit count for project"""
        return len(self.project_commits.get(project_id, []))

# Global storage instance
storage = Storage()
