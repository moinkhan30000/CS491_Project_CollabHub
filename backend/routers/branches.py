from fastapi import APIRouter, HTTPException, status, Depends
from typing import List

from dependencies import get_current_user
from entities.user_entity import User
from repositories.project_repository import ProjectRepository
from repositories.branch_repository import BranchRepository
from repositories.commit_repository import CommitRepository
from schemas.branch_schema import BranchCreate, BranchDetail

router = APIRouter()
project_repo = ProjectRepository()
branch_repo = BranchRepository()
commit_repo = CommitRepository()

@router.get("", response_model=List[BranchDetail])
async def list_branches(project_id: str, current_user: User = Depends(get_current_user)):
    """List all branches for a project."""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")
        
    branches = branch_repo.get_project_branches(project_id)
    return branches

@router.post("", response_model=BranchDetail, status_code=status.HTTP_201_CREATED)
async def create_branch(project_id: str, branch_data: BranchCreate, current_user: User = Depends(get_current_user)):
    """Create a new branch."""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    existing_branch = branch_repo.get_branch(project_id, branch_data.name)
    if existing_branch:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=f"Branch '{branch_data.name}' already exists.")

    if branch_data.headCommitId:
        commit = commit_repo.get_commit(branch_data.headCommitId)
        if not commit or commit.projectId != project_id:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Head commit not found")

    branch = branch_repo.create_branch(
        project_id=project_id, 
        name=branch_data.name, 
        user_id=current_user.userId, 
        head_commit_id=branch_data.headCommitId
    )
    return branch

@router.get("/{branch_name}", response_model=BranchDetail)
async def get_branch(project_id: str, branch_name: str, current_user: User = Depends(get_current_user)):
    """Get details of a specific branch."""
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")

    branch = branch_repo.get_branch(project_id, branch_name)
    if not branch:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Branch not found")

    return branch
