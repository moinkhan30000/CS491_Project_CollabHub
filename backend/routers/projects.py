from fastapi import APIRouter, HTTPException, status, UploadFile, File, Form, Depends
from fastapi.responses import FileResponse
from typing import List, Optional
from schemas.project_schema import ProjectCreate
from entities.project_entity import Project
from entities.user_entity import User
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository
from repositories.project_member_repository import ProjectMemberRepository
from repositories.user_repository import UserRepository
from services.storage import StorageService
from dependencies import get_current_user
import shutil
import os

router = APIRouter()
project_repo = ProjectRepository()
commit_repo = CommitRepository()
member_repo = ProjectMemberRepository()
user_repo = UserRepository()
storage_service = StorageService()

# --- LIST PROJECTS ---

@router.get("")
async def list_projects(current_user: User = Depends(get_current_user)):
    """List all projects where the current user is an active member"""
    projects = member_repo.get_user_projects(current_user.userId)
    return {"projects": projects}

# --- INITIALIZATION ---

@router.post("/init", status_code=status.HTTP_201_CREATED)
async def init_project(
    name: str = Form(...),
    description: Optional[str] = Form(None),
    file: UploadFile = File(...),
    current_user: User = Depends(get_current_user)
):
    """
    Initialize a new project:
    1. Create Project
    2. Upload Initial File
    3. Create Initial Commit
    4. Set Owner
    """
    user_id = current_user.userId
    
    # 1. Create Project
    project = project_repo.create_project(name, description, created_by=user_id)
    
    # 2. Upload File
    import uuid
    commit_uuid = str(uuid.uuid4())
    
    storage_url = await storage_service.upload_file(file, project.projectId, commit_uuid)
    
    # 3. Create Initial Commit
    commit_repo.create_commit(
        project_id=project.projectId,
        model_id="init",
        message="Initial Commit",
        author=user_id,
        storage_url=storage_url,
        change_type="ADD"
    )
    
    # 4. Set Owner
    member_repo.add_member(project.projectId, user_id, role="OWNER", status="ACTIVE")
    
    return {"projectId": project.projectId, "name": project.name, "status": "Initialized"}

# --- INVITATIONS ---

@router.post("/{project_id}/invite")
async def invite_user(project_id: str, email: str):
    """Invite a user to the project by email"""
    user = user_repo.get_user_by_email(email)
    if not user:
        raise HTTPException(status_code=404, detail="User not found")
        
    # Check if already member
    existing = member_repo.get_member(project_id, user.userId)
    if existing:
        raise HTTPException(status_code=400, detail="User is already a member or invited")
        
    member_repo.add_member(project_id, user.userId, role="COLLABORATOR", status="PENDING")
    return {"message": f"Invitation sent to {email}"}

@router.get("/invitations/pending")
async def get_pending_invites(current_user: User = Depends(get_current_user)):
    """Get pending invitations for the current user"""
    return member_repo.get_pending_invites(current_user.userId)

@router.post("/invitations/{invite_id}/respond")
async def respond_invitation(invite_id: int, status: str): # status: ACTIVE or DECLINED
    """
    Accept or Decline invitation.
    If ACCEPTED, returns the latest project file.
    """
    member = member_repo.update_status(invite_id, status)
    if not member:
         raise HTTPException(status_code=404, detail="Invitation not found")
         
    if status == "ACTIVE":
        # Auto-Download Logic
        latest_commit = commit_repo.get_latest_commit(member.projectId)
        if latest_commit and latest_commit.storageUrl:
            file_path = storage_service.get_file_path(latest_commit.storageUrl)
            if file_path.exists():
                return FileResponse(
                    path=file_path, 
                    filename=f"{member.projectId}.rvt", 
                    media_type='application/octet-stream'
                )
    
    return {"status": status, "message": "Invitation updated"}
