"""
Projects Router
"""

from fastapi import APIRouter, HTTPException, status
from typing import List
from models import Project, ProjectCreate
from storage import storage

router = APIRouter()

@router.get("/", response_model=dict)
async def list_projects():
    """List all projects"""
    projects = storage.list_projects()
    return {"projects": projects}

@router.post("/", response_model=Project, status_code=status.HTTP_201_CREATED)
async def create_project(project_data: ProjectCreate):
    """Create a new project"""
    
    # In production, get user_id from JWT token
    user_id = "default-user"
    
    project = storage.create_project(
        name=project_data.name,
        description=project_data.description,
        created_by=user_id
    )
    
    return project

@router.get("/{project_id}", response_model=dict)
async def get_project(project_id: str):
    """Get project details"""
    
    project = storage.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )
    
    commit_count = storage.get_commit_count(project_id)
    
    return {
        **project.model_dump(),
        "statistics": {
            "totalCommits": commit_count,
            "totalElements": 0,  # Would compute from latest snapshot
            "storageUsed": 0
        }
    }
