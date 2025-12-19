"""
Projects Router
"""

from fastapi import APIRouter, HTTPException, status
from typing import List
from models import ProjectCreate
from entities.project_entity import Project
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository

router = APIRouter()
project_repo = ProjectRepository()
commit_repo = CommitRepository()

@router.get("/", response_model=dict)
async def list_projects():
    """List all projects"""
    projects = project_repo.list_projects()
    return {"projects": projects}

@router.post("/", response_model=Project, status_code=status.HTTP_201_CREATED)
async def create_project(project_data: ProjectCreate):
    """Create a new project"""
    
    # In production, get user_id from JWT token
    user_id = "default-user"
    
    project = project_repo.create_project(
        name=project_data.name,
        description=project_data.description,
        created_by=user_id
    )
    
    return project

@router.get("/{project_id}", response_model=dict)
async def get_project(project_id: str):
    """Get project details"""
    
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )
    
    commit_count = commit_repo.get_commit_count(project_id)
    
    return {
        **project.model_dump(),
        "statistics": {
            "totalCommits": commit_count,
            "totalElements": 0,  # Would compute from latest snapshot
            "storageUsed": 0
        }
    }
