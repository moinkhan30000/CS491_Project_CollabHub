"""
Payload donor RVT storage endpoints.
"""

import json
import os
from datetime import datetime
from pathlib import Path
from typing import Optional

from fastapi import APIRouter, Depends, File, Form, HTTPException, Query, UploadFile, status
from fastapi.responses import FileResponse

from dependencies import get_current_user
from entities.user_entity import User
from repositories.project_repository import ProjectRepository

router = APIRouter()
project_repo = ProjectRepository()
_BASE_DIR = None


def _payload_base_dir() -> Path:
    global _BASE_DIR
    if _BASE_DIR is not None:
        return _BASE_DIR

    root = os.environ.get("DATA_DIR")
    if not root:
        root = os.path.join(os.getcwd(), "data")
    path = Path(root) / "payloads"
    path.mkdir(parents=True, exist_ok=True)
    _BASE_DIR = path
    return path


def _payload_project_dir(project_id: str) -> Path:
    path = _payload_base_dir() / project_id
    path.mkdir(parents=True, exist_ok=True)
    return path


def _metadata_path(project_id: str, payload_id: str) -> Path:
    return _payload_project_dir(project_id) / f"{payload_id}.json"


def _load_metadata(project_id: str, payload_id: str) -> Optional[dict]:
    path = _metadata_path(project_id, payload_id)
    if not path.exists():
        return None

    try:
        return json.loads(path.read_text())
    except Exception:
        return None


def _find_payload_file(project_id: str, payload_id: str) -> Optional[Path]:
    project_dir = _payload_project_dir(project_id)
    matches = list(project_dir.glob(f"{payload_id}.*"))
    matches = [match for match in matches if match.is_file() and match.suffix.lower() != ".json"]
    return matches[0] if matches else None


def _build_payload_ref(project_id: str, payload_id: str, metadata: Optional[dict]) -> dict:
    metadata = metadata or {}
    return {
        "payloadId": payload_id,
        "storageUrl": metadata.get("storageUrl"),
        "contentHash": metadata.get("contentHash", payload_id),
        "categories": metadata.get("categories", []),
        "markers": metadata.get("markers", []),
    }


def _validate_project(project_id: str) -> None:
    project = project_repo.get_project(project_id)
    if not project:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Project not found")


@router.get("/{project_id}/payloads/status")
async def payload_status(
    project_id: str,
    contentHash: str = Query(...),
    current_user: User = Depends(get_current_user),
):
    _validate_project(project_id)

    metadata = _load_metadata(project_id, contentHash)
    payload_file = _find_payload_file(project_id, contentHash)
    exists = metadata is not None and payload_file is not None
    return {
        "exists": exists,
        "payload": _build_payload_ref(project_id, contentHash, metadata) if exists else None,
    }


@router.post("/{project_id}/payloads", status_code=status.HTTP_201_CREATED)
async def upload_payload(
    project_id: str,
    contentHash: str = Form(...),
    categoriesJson: Optional[str] = Form("[]"),
    markersJson: Optional[str] = Form("[]"),
    file: UploadFile = File(...),
    current_user: User = Depends(get_current_user),
):
    _validate_project(project_id)

    if not file.filename:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Missing filename")

    try:
        categories = json.loads(categoriesJson or "[]")
    except Exception as exc:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=f"Invalid categoriesJson: {exc}")

    try:
        markers = json.loads(markersJson or "[]")
    except Exception as exc:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=f"Invalid markersJson: {exc}")

    existing_metadata = _load_metadata(project_id, contentHash)
    existing_file = _find_payload_file(project_id, contentHash)
    if existing_metadata is not None and existing_file is not None:
        return _build_payload_ref(project_id, contentHash, existing_metadata)

    project_dir = _payload_project_dir(project_id)
    extension = Path(file.filename).suffix or ".rvt"
    payload_path = project_dir / f"{contentHash}{extension}"

    with payload_path.open("wb") as handle:
        while True:
            chunk = await file.read(1024 * 1024)
            if not chunk:
                break
            handle.write(chunk)

    metadata = {
        "projectId": project_id,
        "payloadId": contentHash,
        "contentHash": contentHash,
        "storageUrl": f"payloads/{project_id}/{payload_path.name}",
        "originalFileName": file.filename,
        "storedFileName": payload_path.name,
        "categories": categories if isinstance(categories, list) else [],
        "markers": markers if isinstance(markers, list) else [],
        "uploadedAt": datetime.utcnow().isoformat() + "Z",
    }
    _metadata_path(project_id, contentHash).write_text(json.dumps(metadata, indent=2))
    return _build_payload_ref(project_id, contentHash, metadata)


@router.get("/{project_id}/payloads/{payload_id}")
async def download_payload(
    project_id: str,
    payload_id: str,
    current_user: User = Depends(get_current_user),
):
    _validate_project(project_id)

    payload_file = _find_payload_file(project_id, payload_id)
    if payload_file is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Payload not found")

    return FileResponse(
        path=str(payload_file),
        filename=payload_file.name,
        media_type="application/octet-stream",
    )
