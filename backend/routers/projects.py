from fastapi import APIRouter, HTTPException, status, UploadFile, File, Form, Depends
from fastapi.responses import FileResponse
from typing import List, Optional
from datetime import datetime
from schemas.project_schema import ProjectCreate
from schemas.element_schema import ElementSnapshot
from entities.project_entity import Project
from entities.user_entity import User
from repositories.project_repository import ProjectRepository
from repositories.commit_repository import CommitRepository
from repositories.project_member_repository import ProjectMemberRepository
from repositories.user_repository import UserRepository
from services.storage import StorageService
from dependencies import get_current_user
from routers.base_files import find_base_file_path, save_base_file
import shutil
import os

router = APIRouter()
project_repo = ProjectRepository()
commit_repo = CommitRepository()
member_repo = ProjectMemberRepository()
user_repo = UserRepository()
storage_service = StorageService()

@router.get("")
async def list_projects(current_user: User = Depends(get_current_user)):
    projects = member_repo.get_user_projects(current_user.userId)
    return {"projects": projects}

@router.post("/init", status_code=status.HTTP_201_CREATED)
async def init_project(
    name: str = Form(...),
    description: Optional[str] = Form(None),
    modelId: str = Form(...),
    file: UploadFile = File(...),
    current_user: User = Depends(get_current_user)
):
    user_id = current_user.userId

    project = project_repo.create_project(name, description, created_by=user_id)

    await save_base_file(project.projectId, modelId, file)

    empty_snapshot = ElementSnapshot(
        version="1.0",
        projectId=project.projectId,
        modelId=modelId,
        timestamp=datetime.utcnow(),
        userName=current_user.fullName,
        commitMessage="Initial Commit",
        elements=[]
    )

    commit_repo.create_commit(
        project_id=project.projectId,
        model_id=modelId,
        message="Initial Commit",
        author=user_id,
        storage_url=None,
        change_type="ADD",
        snapshot=empty_snapshot,
        element_count=0,
        changed_elements=0
    )

    member_repo.add_member(project.projectId, user_id, role="OWNER", status="ACTIVE")

    return {"projectId": project.projectId, "name": project.name, "status": "Initialized"}

@router.post("/{project_id}/invite")
async def invite_user(project_id: str, email: str):
    user = user_repo.get_user_by_email(email)
    if not user:
        raise HTTPException(status_code=404, detail="User not found")
    existing = member_repo.get_member(project_id, user.userId)
    if existing:
        raise HTTPException(status_code=400, detail="User is already a member or invited")
    member_repo.add_member(project_id, user.userId, role="COLLABORATOR", status="PENDING")
    return {"message": f"Invitation sent to {email}"}

@router.get("/invitations/pending")
async def get_pending_invites(current_user: User = Depends(get_current_user)):
    return member_repo.get_pending_invites(current_user.userId)

@router.post("/invitations/{invite_id}/respond")
async def respond_invitation(invite_id: int, status: str):
    member = member_repo.update_status(invite_id, status)
    if not member:
        raise HTTPException(status_code=404, detail="Invitation not found")

    if status == "ACTIVE":
        base_dir = os.path.join(os.getenv("DATA_DIR", os.path.join(os.getcwd(), "data")), "base_files", member.projectId)
        if os.path.isdir(base_dir):
            candidates = [
                os.path.join(base_dir, name)
                for name in os.listdir(base_dir)
                if not name.endswith(".json")
            ]
            candidates = [path for path in candidates if os.path.isfile(path)]

            if candidates:
                file_path = max(candidates, key=lambda p: os.path.getsize(p))
                if os.path.getsize(file_path) < 10240:
                    raise HTTPException(status_code=500, detail="Base file is too small; upload may have failed.")

                project = project_repo.get_project(member.projectId)
                project_name = project.name if project else member.projectId
                ext = os.path.splitext(file_path)[1] or ".rvt"
                return FileResponse(
                    path=file_path,
                    filename=f"{project_name}{ext}",
                    media_type='application/octet-stream'
                )

        raise HTTPException(status_code=404, detail="Project file not found for invitation.")

    return {"status": status, "message": "Invitation updated"}
