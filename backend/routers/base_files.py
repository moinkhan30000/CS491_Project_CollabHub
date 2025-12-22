"""
Base file (RVT/RFA) storage endpoints.
"""

import hashlib
import json
import os
from datetime import datetime
from pathlib import Path
from typing import Optional

from fastapi import APIRouter, HTTPException, Query, UploadFile, File, status, Depends
from fastapi.responses import FileResponse

from entities.user_entity import User
from repositories.project_repository import ProjectRepository
from dependencies import get_current_user

router = APIRouter()
project_repo = ProjectRepository()
_BASE_DIR = None

def _base_dir() -> Path:
    global _BASE_DIR
    if _BASE_DIR is not None:
        return _BASE_DIR
    root = os.environ.get("BASE_FILE_DIR") or os.environ.get("DATA_DIR")
    if not root:
        root = os.path.join(os.getcwd(), "data")
    root = os.path.join(root, "base_files")
    path = Path(root)
    path.mkdir(parents=True, exist_ok=True)
    _BASE_DIR = path
    return path

def _model_hash(model_id: str) -> str:
    return hashlib.sha256(model_id.encode("utf-8")).hexdigest()

def _project_dir(project_id: str) -> Path:
    path = _base_dir() / project_id
    path.mkdir(parents=True, exist_ok=True)
    return path

def _find_base_file(project_id: str, model_id: str) -> Optional[Path]:
    project_dir = _project_dir(project_id)
    model_key = _model_hash(model_id)
    matches = list(project_dir.glob(f"{model_key}.*"))
    return matches[0] if matches else None

def find_base_file_path(project_id: str, model_id: str) -> Optional[Path]:
    return _find_base_file(project_id, model_id)

async def save_base_file(project_id: str, model_id: str, file: UploadFile) -> Path:
    project_dir = _project_dir(project_id)
    model_key = _model_hash(model_id)
    extension = Path(file.filename).suffix or ".rvt"
    dest_path = project_dir / f"{model_key}{extension}"

    try:
        await file.seek(0)
    except Exception:
        pass

    with dest_path.open("wb") as f:
        while True:
            chunk = await file.read(1024 * 1024)
            if not chunk:
                break
            f.write(chunk)

    metadata_path = project_dir / f"{model_key}.json"
    metadata = {
        "projectId": project_id,
        "modelId": model_id,
        "originalFileName": file.filename,
        "storedFileName": dest_path.name,
        "uploadedAt": datetime.utcnow().isoformat() + "Z"
    }
    metadata_path.write_text(json.dumps(metadata, indent=2))

    return dest_path

def _validate_project(project_id: str) -> None:
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Project not found"
        )

@router.get("/{project_id}/base-file/status")
async def base_file_status(
    project_id: str,
    modelId: str = Query(...),
    current_user: User = Depends(get_current_user)
):
    _validate_project(project_id)
    existing = _find_base_file(project_id, modelId)
    return {
        "exists": existing is not None,
        "fileName": existing.name if existing else None
    }

@router.post("/{project_id}/base-file", status_code=status.HTTP_201_CREATED)
async def upload_base_file(
    project_id: str,
    modelId: str = Query(...),
    file: UploadFile = File(...),
    current_user: User = Depends(get_current_user)
):
    _validate_project(project_id)

    if not file.filename:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Missing filename"
        )

    project_dir = _project_dir(project_id)
    model_key = _model_hash(modelId)
    extension = Path(file.filename).suffix or ".rvt"
    dest_path = project_dir / f"{model_key}{extension}"

    with dest_path.open("wb") as f:
        while True:
            chunk = await file.read(1024 * 1024)
            if not chunk:
                break
            f.write(chunk)

    metadata_path = project_dir / f"{model_key}.json"
    metadata = {
        "projectId": project_id,
        "modelId": modelId,
        "originalFileName": file.filename,
        "storedFileName": dest_path.name,
        "uploadedAt": datetime.utcnow().isoformat() + "Z"
    }
    metadata_path.write_text(json.dumps(metadata, indent=2))

    return {
        "storedFileName": dest_path.name,
        "modelId": modelId
    }

@router.get("/{project_id}/base-file")
async def download_base_file(
    project_id: str,
    modelId: str = Query(...),
    current_user: User = Depends(get_current_user)
):
    _validate_project(project_id)
    existing = _find_base_file(project_id, modelId)
    if not existing:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Base file not found"
        )

    return FileResponse(
        path=str(existing),
        filename=existing.name,
        media_type="application/octet-stream"
    )
